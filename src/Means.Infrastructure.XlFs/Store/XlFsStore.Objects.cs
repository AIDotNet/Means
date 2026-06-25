using System.Security.Cryptography;
using System.Text.Json;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.BucketPrefix, 100_000, null, cancellationToken);
        return rows.Select(row => Deserialize<XlBucketRecord>(row.Value))
            .OrderBy(bucket => bucket.Name, StringComparer.Ordinal)
            .Select(bucket => new BucketInfo(bucket.Name, bucket.CreatedAt))
            .ToArray();
    }

    public async Task<BucketInfo> CreateBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (await BucketExistsAsync(bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.BucketAlreadyExists, "Bucket already exists.", 409);
        }

        var bucket = new XlBucketRecord(bucketName, DateTimeOffset.UtcNow);
        await Db.PutJsonAsync(Keys.Bucket(bucketName), bucket, cancellationToken);
        return new BucketInfo(bucket.Name, bucket.CreatedAt);
    }

    public async Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var bucket = await Db.GetJsonAsync<XlBucketRecord>(Keys.Bucket(bucketName), cancellationToken);
        return bucket is null ? null : new BucketInfo(bucket.Name, bucket.CreatedAt);
    }

    public async Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        if ((await Db.ScanPrefixAsync(Keys.CurrentObjectPrefix(bucketName), 1, null, cancellationToken)).Count > 0
            || (await Db.ScanPrefixAsync(Keys.MultipartUploadPrefix(bucketName), 1, null, cancellationToken)).Count > 0)
        {
            throw new MeansException(MeansErrorCodes.BucketNotEmpty, "Bucket is not empty.", 409);
        }

        await Db.PutBatchAsync([new LogDbMutation(Keys.Bucket(bucketName), null, true)], cancellationToken);
    }

    public async Task<ListObjectsResult> ListObjectsAsync(
        string bucketName,
        ListObjectsOptions options,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var maxKeys = Math.Clamp(options.MaxKeys, 1, 1000);
        var prefix = options.Prefix ?? string.Empty;
        var dbPrefix = Keys.CurrentObjectPrefix(bucketName) + Escape(prefix);
        var rows = await Db.ScanPrefixAsync(dbPrefix, maxKeys + 1, DecodeToken(options.ContinuationToken), cancellationToken);
        var objects = new List<ListedObject>();
        var commonPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? nextToken = null;
        string? lastScannedKey = DecodeToken(options.ContinuationToken);
        foreach (var row in rows)
        {
            if (objects.Count + commonPrefixes.Count >= maxKeys)
            {
                nextToken = lastScannedKey is null ? null : EncodeToken(lastScannedKey);
                break;
            }

            lastScannedKey = row.Key;
            var record = Deserialize<XlObjectRecord>(row.Value);
            if (record.IsDeleteMarker)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(options.Delimiter))
            {
                var rest = record.Key.Length >= prefix.Length ? record.Key[prefix.Length..] : record.Key;
                var delimiterIndex = rest.IndexOf(options.Delimiter, StringComparison.Ordinal);
                if (delimiterIndex >= 0)
                {
                    commonPrefixes.Add(prefix + rest[..(delimiterIndex + options.Delimiter.Length)]);
                    continue;
                }
            }

            objects.Add(new ListedObject(record.Key, record.ETag, record.ContentLength, record.LastModified, record.ContentType));
        }

        return new ListObjectsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            objects.Count + commonPrefixes.Count,
            nextToken is not null,
            nextToken,
            objects,
            commonPrefixes.ToArray());
    }

    public async Task<ListObjectVersionsResult> ListObjectVersionsAsync(
        string bucketName,
        ListObjectVersionsOptions options,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var maxKeys = Math.Clamp(options.MaxKeys, 1, 1000);
        var prefix = options.Prefix ?? string.Empty;
        var dbPrefix = Keys.VersionPrefix(bucketName) + Escape(prefix);
        var rows = await Db.ScanPrefixAsync(dbPrefix, 100_000, null, cancellationToken);
        var page = ApplyVersionMarker(
                rows.Select(row => Deserialize<XlObjectRecord>(row.Value))
                    .GroupBy(record => record.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .SelectMany(group => group.OrderByDescending(record => record.LastModified)),
                options.KeyMarker,
                options.VersionIdMarker)
            .Take(maxKeys + 1)
            .ToList();

        var commonPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        var visiblePage = new List<XlObjectRecord>(maxKeys);
        foreach (var record in page)
        {
            if (visiblePage.Count + commonPrefixes.Count >= maxKeys)
            {
                break;
            }

            if (!string.IsNullOrEmpty(options.Delimiter))
            {
                var rest = record.Key.Length >= prefix.Length ? record.Key[prefix.Length..] : record.Key;
                var delimiterIndex = rest.IndexOf(options.Delimiter, StringComparison.Ordinal);
                if (delimiterIndex >= 0)
                {
                    commonPrefixes.Add(prefix + rest[..(delimiterIndex + options.Delimiter.Length)]);
                    continue;
                }
            }

            visiblePage.Add(record);
        }

        var visible = visiblePage.ToArray();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in visible.Select(row => row.Key).Distinct(StringComparer.Ordinal))
        {
            var currentRecord = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken);
            if (currentRecord is not null)
            {
                current[key] = currentRecord.VersionId;
            }
        }

        var isTruncated = page.Count > maxKeys;
        var lastReturned = visible.LastOrDefault();
        return new ListObjectVersionsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            options.KeyMarker,
            options.VersionIdMarker,
            maxKeys,
            isTruncated,
            isTruncated ? lastReturned?.Key : null,
            isTruncated ? lastReturned?.VersionId : null,
            visible.Select(row => new ListedObjectVersion(
                row.Key,
                row.VersionId,
                current.TryGetValue(row.Key, out var latest) && latest == row.VersionId,
                row.IsDeleteMarker,
                row.ETag,
                row.ContentLength,
                row.LastModified)).ToArray(),
            commonPrefixes.ToArray());
    }

    private static IEnumerable<XlObjectRecord> ApplyVersionMarker(
        IEnumerable<XlObjectRecord> records,
        string? keyMarker,
        string? versionIdMarker)
    {
        if (string.IsNullOrWhiteSpace(keyMarker))
        {
            foreach (var record in records)
            {
                yield return record;
            }

            yield break;
        }

        var markerSeen = false;
        foreach (var record in records)
        {
            var keyComparison = string.CompareOrdinal(record.Key, keyMarker);
            if (keyComparison < 0)
            {
                continue;
            }

            if (keyComparison > 0)
            {
                yield return record;
                continue;
            }

            if (string.IsNullOrWhiteSpace(versionIdMarker))
            {
                continue;
            }

            if (markerSeen)
            {
                yield return record;
                continue;
            }

            if (string.Equals(record.VersionId, versionIdMarker, StringComparison.Ordinal))
            {
                markerSeen = true;
            }
        }
    }

    public async Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(request.BucketName, cancellationToken);
        var objectId = Guid.NewGuid().ToString("N");
        var tempPath = TempPath(objectId + ".put.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        try
        {
            var hash = await WriteTempObjectAsync(request.Content, tempPath, cancellationToken);
            return await CommitObjectFromFileAsync(
                request.BucketName,
                request.Key,
                objectId,
                tempPath,
                hash.ETag,
                hash.ChecksumSha256,
                hash.Length,
                string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
                NormalizeMetadata(request.Metadata),
                request.CacheControl,
                request.ContentDisposition,
                cancellationToken);
        }
        finally
        {
            DeleteFileQuietly(tempPath);
        }
    }

    public Task<ObjectData> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        return GetObjectAsync(bucketName, key, null, cancellationToken);
    }

    public async Task<ObjectData> GetObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        var info = await HeadObjectAsync(bucketName, key, versionId, cancellationToken);
        var manifest = await ReadManifestAsync(info, cancellationToken);
        if (IsReedSolomonErasure(manifest))
        {
            return new ObjectData(info, await OpenErasureCodedObjectAsync(info, manifest, cancellationToken));
        }

        if (manifest.Parts.Count == 1)
        {
            foreach (var shard in manifest.Parts[0].Shards.OrderBy(shard => shard.SetIndex))
            {
                var opened = await TryOpenReadableShardAsync(shard, cancellationToken);
                if (opened.ChecksumMismatch)
                {
                    await QueueHealAsync(info, "ShardChecksumMismatch", cancellationToken);
                    continue;
                }

                if (opened.Stream is not null)
                {
                    return new ObjectData(info, opened.Stream, opened.Path);
                }
            }

            await QueueHealAsync(info, "NoReadableShard", cancellationToken);
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is missing.", 404);
        }

        var segments = new List<ShardReadSegment>(manifest.Parts.Count);
        foreach (var part in manifest.Parts.OrderBy(part => part.PartNumber))
        {
            var resolved = false;
            foreach (var shard in part.Shards.OrderBy(shard => shard.SetIndex))
            {
                var path = await TryResolveReadableShardPathAsync(shard, cancellationToken);
                if (path.ChecksumMismatch)
                {
                    await QueueHealAsync(info, "ShardChecksumMismatch", cancellationToken);
                    continue;
                }

                if (path.Path is not null)
                {
                    segments.Add(new ShardReadSegment(path.Path, part.Size));
                    resolved = true;
                    break;
                }
            }

            if (!resolved)
            {
                await QueueHealAsync(info, "NoReadableMultipartPartShard", cancellationToken);
                throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is missing.", 404);
            }
        }

        return new ObjectData(info, new XlCompositeReadStream(segments));
    }

    public Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        return HeadObjectAsync(bucketName, key, null, cancellationToken);
    }

    public async Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var record = string.IsNullOrWhiteSpace(versionId)
            ? await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken)
            : await Db.GetJsonAsync<XlObjectRecord>(Keys.Version(bucketName, key, versionId), cancellationToken);
        if (record is null || record.IsDeleteMarker)
        {
            throw new MeansException(string.IsNullOrWhiteSpace(versionId) ? MeansErrorCodes.NoSuchKey : MeansErrorCodes.NoSuchVersion, "Object does not exist.", 404);
        }

        return ToObjectInfo(record);
    }

    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        _ = await DeleteObjectAsync(bucketName, key, null, cancellationToken);
    }

    public async Task<DeleteObjectResult> DeleteObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var record = await Db.GetJsonAsync<XlObjectRecord>(Keys.Version(bucketName, key, versionId), cancellationToken)
                ?? throw new MeansException(MeansErrorCodes.NoSuchVersion, "Object version does not exist.", 404);
            var current = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken);
            var mutations = new List<LogDbMutation>
            {
                new(Keys.Version(bucketName, key, versionId), null, true)
            };
            if (current?.VersionId == versionId)
            {
                var replacement = await FindLatestObjectVersionAsync(bucketName, key, versionId, cancellationToken);
                mutations.Add(
                    replacement is null
                        ? new LogDbMutation(Keys.CurrentObject(bucketName, key), null, true)
                        : new LogDbMutation(Keys.CurrentObject(bucketName, key), Serialize(replacement), false));
            }

            await Db.PutBatchAsync(mutations, cancellationToken);

            if (!record.IsDeleteMarker)
            {
                await DeleteObjectFilesQuietlyAsync(record, CancellationToken.None);
            }

            return new DeleteObjectResult(bucketName, key, versionId, record.IsDeleteMarker);
        }

        if (string.Equals(await GetBucketVersioningStatusAsync(bucketName, cancellationToken), BucketVersioningStatuses.Enabled, StringComparison.Ordinal))
        {
            var markerId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var marker = new XlObjectRecord(bucketName, key, markerId, markerId, string.Empty, 0, "application/octet-stream", now, true, new Dictionary<string, string>(), new Dictionary<string, string>(), null, null);
            await Db.PutBatchAsync([
                new LogDbMutation(Keys.Version(bucketName, key, markerId), Serialize(marker), false),
                new LogDbMutation(Keys.CurrentObject(bucketName, key), Serialize(marker), false)
            ], cancellationToken);
            return new DeleteObjectResult(bucketName, key, markerId, true);
        }

        var existing = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken);
        var deleteMutations = new List<LogDbMutation> { new(Keys.CurrentObject(bucketName, key), null, true) };
        if (existing is not null)
        {
            deleteMutations.Add(new LogDbMutation(Keys.Version(bucketName, key, existing.VersionId), null, true));
        }

        await Db.PutBatchAsync(deleteMutations, cancellationToken);
        if (existing is not null)
        {
            await DeleteObjectFilesQuietlyAsync(existing, CancellationToken.None);
        }

        return new DeleteObjectResult(bucketName, key, null, false);
    }

    public async Task<BatchDeleteResult> DeleteObjectsAsync(
        string bucketName,
        IReadOnlyList<BatchDeleteObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var deleted = new List<DeleteObjectResult>(objects.Count);
        var errors = new List<BatchDeleteError>();
        foreach (var identifier in objects)
        {
            try
            {
                var result = await DeleteObjectAsync(bucketName, identifier.Key, identifier.VersionId, cancellationToken);
                deleted.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MeansException ex) when (ex.StatusCode == 404)
            {
                deleted.Add(new DeleteObjectResult(bucketName, identifier.Key, identifier.VersionId, false));
            }
            catch (Exception ex)
            {
                errors.Add(new BatchDeleteError(identifier.Key, identifier.VersionId, ex is MeansException me ? me.Code : "InternalError", ex.Message));
            }
        }

        return new BatchDeleteResult(bucketName, deleted, errors);
    }

    public async Task<ObjectInfo> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken)
    {
        await using var source = await GetObjectAsync(request.SourceBucket, request.SourceKey, request.SourceVersionId, cancellationToken);
        var replace = string.Equals(request.MetadataDirective, CopyMetadataDirectives.Replace, StringComparison.OrdinalIgnoreCase);
        return await PutObjectAsync(new PutObjectRequest(
            request.DestinationBucket,
            request.DestinationKey,
            source.Content,
            string.IsNullOrWhiteSpace(request.ContentType) ? source.Info.ContentType : request.ContentType,
            replace ? request.Metadata : source.Info.Metadata,
            replace ? request.CacheControl : request.CacheControl ?? source.Info.CacheControl,
            replace ? request.ContentDisposition : request.ContentDisposition ?? source.Info.ContentDisposition), cancellationToken);
    }

    private async Task<ObjectInfo> CommitObjectFromFileAsync(
        string bucketName,
        string key,
        string objectId,
        string sourcePath,
        string etag,
        string checksum,
        long length,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        string? cacheControl,
        string? contentDisposition,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var erasureWrite = await TryWriteErasureCodedShardsAsync(
            bucketName,
            objectId,
            sourcePath,
            length,
            cancellationToken);
        var shards = erasureWrite?.Shards
            ?? await WriteFullCopyShardsAsync(
                bucketName,
                key,
                objectId,
                sourcePath,
                setIndex => ObjectRelativePath(bucketName, objectId, setIndex),
                length,
                checksum,
                "Insufficient online disks for write quorum.",
                cancellationToken);

        var record = new XlObjectRecord(bucketName, key, objectId, objectId, etag, length, contentType, now, false, metadata, new Dictionary<string, string>(), cacheControl, contentDisposition);
        var manifest = new XlObjectManifest(
            FormatVersion,
            bucketName,
            key,
            objectId,
            objectId,
            etag,
            length,
            contentType,
            now,
            false,
            erasureWrite?.Erasure ?? new XlErasureInfo(FullCopyAlgorithm, Math.Max(1, _options.ErasureDataShards), Math.Max(0, _options.ErasureParityShards), 128 * 1024, WriteQuorum, ReadQuorum),
            [new XlPartManifest(1, "part.0", length, etag, checksum, shards)],
            metadata,
            new Dictionary<string, string>(),
            cacheControl,
            contentDisposition);
        try
        {
            await WriteManifestCopiesAsync(bucketName, objectId, manifest, shards, cancellationToken);

            var existing = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken);
            await Db.PutBatchAsync([
                new LogDbMutation(Keys.Version(bucketName, key, objectId), Serialize(record), false),
                new LogDbMutation(Keys.CurrentObject(bucketName, key), Serialize(record), false)
            ], cancellationToken);
            if (existing is not null && !string.Equals(await GetBucketVersioningStatusAsync(bucketName, cancellationToken), BucketVersioningStatuses.Enabled, StringComparison.Ordinal))
            {
                await DeleteObjectFilesQuietlyAsync(existing, CancellationToken.None);
            }

            if (erasureWrite is { FailedShardCount: > 0 })
            {
                await TryQueueHealAsync(ToObjectInfo(record), "ErasureWriteDegraded", cancellationToken);
            }

            return ToObjectInfo(record);
        }
        catch
        {
            await DeleteManifestShardFilesQuietlyAsync(manifest, CancellationToken.None);
            await DeleteObjectFilesQuietlyAsync(record, CancellationToken.None);
            throw;
        }
    }

    private async Task<(string ETag, string ChecksumSha256, long Length)> WriteTempObjectAsync(Stream input, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[XlStreamBufferSize];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            md5.AppendData(buffer.AsSpan(0, read));
            sha256.AppendData(buffer.AsSpan(0, read));
            length += read;
        }

        return (
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            length);
    }

    private async Task TryQueueHealAsync(ObjectInfo info, string reason, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await QueueHealAsync(info, reason, cancellationToken);
        }
        catch
        {
        }
    }

    private async Task<string> GetBucketVersioningStatusAsync(string bucketName, CancellationToken cancellationToken)
    {
        return await Db.GetJsonAsync<string>(Keys.BucketVersioning(bucketName), cancellationToken) ?? BucketVersioningStatuses.Off;
    }

    private async Task<XlObjectRecord?> FindLatestObjectVersionAsync(
        string bucketName,
        string key,
        string excludedVersionId,
        CancellationToken cancellationToken)
    {
        var rows = await Db.ScanPrefixAsync(Keys.VersionPrefix(bucketName) + Escape(key) + ":", 100_000, null, cancellationToken);
        return rows.Select(row => Deserialize<XlObjectRecord>(row.Value))
            .Where(record => !string.Equals(record.VersionId, excludedVersionId, StringComparison.Ordinal))
            .OrderByDescending(record => record.LastModified)
            .FirstOrDefault();
    }

    private async Task DeleteObjectFilesQuietlyAsync(XlObjectRecord record, CancellationToken cancellationToken)
    {
        var manifests = new List<XlObjectManifest>();
        var localShardPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteShards = new Dictionary<string, XlShardManifest>(StringComparer.Ordinal);
        var manifestRelativePath = ObjectManifestRelativePath(record.BucketName, record.ObjectId);
        foreach (var disk in _disks)
        {
            var manifestPath = Path.Combine(disk.RootPath, manifestRelativePath);
            try
            {
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), XlJsonContext.Default.XlObjectManifest);
                    if (manifest is not null)
                    {
                        manifests.Add(manifest);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var manifest in manifests)
        {
            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                var shardDisk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (shardDisk is not null)
                {
                    localShardPaths.Add(Path.Combine(shardDisk.RootPath, shard.RelativePath));
                }
                else
                {
                    remoteShards[shard.DiskId + "|" + shard.RelativePath] = shard;
                }
            }
        }

        foreach (var path in localShardPaths)
        {
            DeleteFileQuietly(path);
        }

        foreach (var disk in _disks)
        {
            DeleteFileQuietly(Path.Combine(disk.RootPath, manifestRelativePath));
        }

        foreach (var shard in remoteShards.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
            if (node is null)
            {
                continue;
            }

            try
            {
                await _shardTransport.DeleteShardAsync(node, shard.DiskId, shard.RelativePath, cancellationToken);
                await _shardTransport.DeleteManifestAsync(node, shard.DiskId, manifestRelativePath, cancellationToken);
            }
            catch
            {
            }
        }

        if (manifests.Count > 0)
        {
            return;
        }

        foreach (var disk in _disks)
        {
            DeleteDirectoryQuietly(Path.Combine(disk.RootPath, "objects", BucketHash(record.BucketName), record.ObjectId));
        }
    }

    private async Task DeleteManifestShardFilesQuietlyAsync(
        XlObjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestRelativePath = ObjectManifestRelativePath(manifest.BucketName, manifest.ObjectId);
        var remoteDiskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is not null)
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
                continue;
            }

            remoteDiskIds.Add(shard.DiskId);
            var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
            if (node is null)
            {
                continue;
            }

            try
            {
                await _shardTransport.DeleteShardAsync(node, shard.DiskId, shard.RelativePath, cancellationToken);
            }
            catch
            {
            }
        }

        foreach (var disk in _disks)
        {
            DeleteFileQuietly(Path.Combine(disk.RootPath, manifestRelativePath));
        }

        foreach (var diskId in remoteDiskIds)
        {
            var node = await TryFindRemoteNodeForDiskAsync(diskId, cancellationToken);
            if (node is null)
            {
                continue;
            }

            try
            {
                await _shardTransport.DeleteManifestAsync(node, diskId, manifestRelativePath, cancellationToken);
            }
            catch
            {
            }
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
