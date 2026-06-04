using System.Security.Cryptography;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task<MultipartUploadInfo> InitiateMultipartUploadAsync(
        InitiateMultipartUploadRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(request.BucketName, cancellationToken);

        var upload = new XlMultipartUploadRecord(
            request.BucketName,
            request.Key,
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            DateTimeOffset.UtcNow,
            NormalizeMetadata(request.Metadata),
            request.CacheControl,
            request.ContentDisposition);

        await Db.PutJsonAsync(Keys.MultipartUpload(upload.BucketName, upload.Key, upload.UploadId), upload, cancellationToken);
        return ToMultipartUploadInfo(upload);
    }

    public async Task<MultipartPartInfo> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(request.BucketName, cancellationToken);
        ValidatePartNumber(request.PartNumber);

        var upload = await ReadMultipartUploadAsync(request.BucketName, request.Key, request.UploadId, cancellationToken)
            ?? throw NoSuchUpload();

        var partId = Guid.NewGuid().ToString("N");
        var tempPath = TempPath(partId + ".part.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            var hash = await WriteTempObjectAsync(request.Content, tempPath, cancellationToken);
            var shards = await WriteFullCopyShardsAsync(
                tempPath,
                disk => MultipartPartRelativePath(upload.BucketName, upload.UploadId, partId, disk.SetIndex),
                hash.Length,
                hash.ChecksumSha256,
                "Insufficient online disks for multipart part write quorum.",
                cancellationToken);

            var existing = await Db.GetJsonAsync<XlMultipartPartRecord>(
                Keys.MultipartPart(upload.BucketName, upload.Key, upload.UploadId, request.PartNumber),
                cancellationToken);

            var record = new XlMultipartPartRecord(
                upload.BucketName,
                upload.Key,
                upload.UploadId,
                request.PartNumber,
                partId,
                hash.ETag,
                hash.Length,
                DateTimeOffset.UtcNow,
                shards[0].RelativePath,
                hash.ChecksumSha256,
                shards);

            await Db.PutJsonAsync(
                Keys.MultipartPart(upload.BucketName, upload.Key, upload.UploadId, request.PartNumber),
                record,
                cancellationToken);

            if (existing is not null)
            {
                DeleteMultipartPartFilesQuietly(existing);
            }

            return ToMultipartPartInfo(record);
        }
        finally
        {
            DeleteFileQuietly(tempPath);
        }
    }

    public async Task<ObjectInfo> CompleteMultipartUploadAsync(
        CompleteMultipartUploadRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(request.BucketName, cancellationToken);
        ValidateCompleteParts(request.Parts);

        var upload = await ReadMultipartUploadAsync(request.BucketName, request.Key, request.UploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var storedParts = (await ReadMultipartPartsAsync(upload.BucketName, upload.Key, upload.UploadId, 0, int.MaxValue, cancellationToken))
            .ToDictionary(part => part.PartNumber);

        var ordered = new List<XlMultipartPartRecord>(request.Parts.Count);
        for (var index = 0; index < request.Parts.Count; index++)
        {
            var requested = request.Parts[index];
            if (!storedParts.TryGetValue(requested.PartNumber, out var stored)
                || !string.Equals(stored.ETag, NormalizeEtag(requested.ETag), StringComparison.OrdinalIgnoreCase))
            {
                throw new MeansException(MeansErrorCodes.InvalidPart, "One or more of the specified parts could not be found.", 400);
            }

            if (index < request.Parts.Count - 1 && stored.Size < MinimumMultipartPartSize)
            {
                throw new MeansException(MeansErrorCodes.EntityTooSmall, "Your proposed upload is smaller than the minimum allowed object size.", 400);
            }

            ordered.Add(stored);
        }

        var objectId = Guid.NewGuid().ToString("N");
        using var multipartMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long totalLength = 0;
        var manifestParts = new List<XlPartManifest>(ordered.Count);
        foreach (var part in ordered)
        {
            multipartMd5.AppendData(HexToBytes(part.ETag));
            totalLength += part.Size;
            manifestParts.Add(new XlPartManifest(
                part.PartNumber,
                "part." + part.PartNumber.ToString("D5"),
                part.Size,
                part.ETag,
                part.ChecksumSha256,
                part.Shards));
        }

        var etag = Convert.ToHexString(multipartMd5.GetHashAndReset()).ToLowerInvariant() + "-" + ordered.Count;
        var now = DateTimeOffset.UtcNow;
        var record = new XlObjectRecord(
            upload.BucketName,
            upload.Key,
            objectId,
            objectId,
            etag,
            totalLength,
            upload.ContentType,
            now,
            false,
            upload.Metadata,
            new Dictionary<string, string>(),
            upload.CacheControl,
            upload.ContentDisposition);
        var manifest = new XlObjectManifest(
            FormatVersion,
            upload.BucketName,
            upload.Key,
            objectId,
            objectId,
            etag,
            totalLength,
            upload.ContentType,
            now,
            false,
            new XlErasureInfo("full-copy-v1", Math.Max(1, _options.ErasureDataShards), Math.Max(0, _options.ErasureParityShards), 128 * 1024, WriteQuorum, ReadQuorum),
            manifestParts,
            upload.Metadata,
            new Dictionary<string, string>(),
            upload.CacheControl,
            upload.ContentDisposition);

        var manifestShards = manifestParts
            .SelectMany(part => part.Shards)
            .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(shard => shard.SetIndex).First())
            .ToArray();
        await WriteManifestCopiesAsync(upload.BucketName, objectId, manifest, manifestShards, cancellationToken);

        var existing = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(upload.BucketName, upload.Key), cancellationToken);
        var mutations = new List<LogDbMutation>
        {
            new(Keys.Version(upload.BucketName, upload.Key, objectId), Serialize(record), false),
            new(Keys.CurrentObject(upload.BucketName, upload.Key), Serialize(record), false),
            new(Keys.MultipartUpload(upload.BucketName, upload.Key, upload.UploadId), null, true)
        };
        mutations.AddRange(storedParts.Values.Select(part =>
            new LogDbMutation(Keys.MultipartPart(upload.BucketName, upload.Key, upload.UploadId, part.PartNumber), null, true)));
        await Db.PutBatchAsync(mutations, cancellationToken);

        if (existing is not null && !string.Equals(await GetBucketVersioningStatusAsync(upload.BucketName, cancellationToken), BucketVersioningStatuses.Enabled, StringComparison.Ordinal))
        {
            await DeleteObjectFilesQuietlyAsync(existing, CancellationToken.None);
        }

        var completedPartNumbers = ordered.Select(part => part.PartNumber).ToHashSet();
        foreach (var part in storedParts.Values)
        {
            if (!completedPartNumbers.Contains(part.PartNumber))
            {
                DeleteMultipartPartFilesQuietly(part);
            }
        }

        return ToObjectInfo(record);
    }

    public async Task AbortMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var upload = await ReadMultipartUploadAsync(bucketName, key, uploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var parts = await ReadMultipartPartsAsync(bucketName, key, uploadId, 0, int.MaxValue, cancellationToken);
        var mutations = new List<LogDbMutation>
        {
            new(Keys.MultipartUpload(upload.BucketName, upload.Key, upload.UploadId), null, true)
        };
        mutations.AddRange(parts.Select(part =>
            new LogDbMutation(Keys.MultipartPart(upload.BucketName, upload.Key, upload.UploadId, part.PartNumber), null, true)));
        await Db.PutBatchAsync(mutations, cancellationToken);

        foreach (var part in parts)
        {
            DeleteMultipartPartFilesQuietly(part);
        }
    }

    public async Task<ListPartsResult> ListPartsAsync(
        string bucketName,
        string key,
        string uploadId,
        int partNumberMarker,
        int maxParts,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var upload = await ReadMultipartUploadAsync(bucketName, key, uploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var effectiveMaxParts = maxParts <= 0 ? 1000 : Math.Min(maxParts, 1000);
        var parts = await ReadMultipartPartsAsync(bucketName, key, uploadId, Math.Max(0, partNumberMarker), effectiveMaxParts + 1, cancellationToken);
        var visible = parts.Take(effectiveMaxParts).Select(ToMultipartPartInfo).ToArray();
        var isTruncated = parts.Count > effectiveMaxParts;
        return new ListPartsResult(
            upload.BucketName,
            upload.Key,
            upload.UploadId,
            upload.InitiatedAt,
            partNumberMarker,
            isTruncated && visible.Length > 0 ? visible[^1].PartNumber : 0,
            effectiveMaxParts,
            isTruncated,
            visible);
    }

    public async Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(
        string bucketName,
        ListMultipartUploadsOptions options,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var maxUploads = options.MaxUploads <= 0 ? 1000 : Math.Min(options.MaxUploads, 1000);
        var prefix = options.Prefix ?? string.Empty;
        var delimiter = options.Delimiter ?? string.Empty;
        var dbPrefix = Keys.MultipartUploadPrefix(bucketName) + Escape(prefix);
        var uploads = new List<MultipartUploadSummary>();
        var commonPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? lastReturnedKey = null;
        string? lastReturnedUploadId = null;
        var returned = 0;
        var scanAfter = MultipartUploadScanAfterKey(bucketName, options.KeyMarker, options.UploadIdMarker);
        var exhausted = false;
        var isTruncated = false;
        while (!isTruncated && !exhausted)
        {
            var batchSize = Math.Min(Math.Max(maxUploads * 4, 128), 4096);
            var rows = await Db.ScanPrefixAsync(dbPrefix, batchSize, scanAfter, cancellationToken);
            if (rows.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var row in rows)
            {
                scanAfter = row.Key;
                var upload = Deserialize<XlMultipartUploadRecord>(row.Value);

                if (!string.IsNullOrEmpty(delimiter))
                {
                    var rest = upload.Key.Length >= prefix.Length ? upload.Key[prefix.Length..] : upload.Key;
                    var delimiterIndex = rest.IndexOf(delimiter, StringComparison.Ordinal);
                    if (delimiterIndex >= 0)
                    {
                        if (commonPrefixes.Add(prefix + rest[..(delimiterIndex + delimiter.Length)]))
                        {
                            if (returned >= maxUploads)
                            {
                                isTruncated = true;
                                break;
                            }

                            lastReturnedKey = upload.Key;
                            lastReturnedUploadId = upload.UploadId;
                            returned++;
                        }

                        continue;
                    }
                }

                if (returned >= maxUploads)
                {
                    isTruncated = true;
                    break;
                }

                uploads.Add(new MultipartUploadSummary(upload.Key, upload.UploadId, upload.InitiatedAt));
                lastReturnedKey = upload.Key;
                lastReturnedUploadId = upload.UploadId;
                returned++;
            }

            exhausted = rows.Count < batchSize;
        }

        return new ListMultipartUploadsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            options.KeyMarker,
            options.UploadIdMarker,
            maxUploads,
            isTruncated,
            isTruncated ? lastReturnedKey : null,
            isTruncated ? lastReturnedUploadId : null,
            uploads,
            commonPrefixes.ToArray());
    }

    public async Task<int> CleanupMultipartUploadsAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var deleted = 0;
        string? afterKey = null;
        while (true)
        {
            var rows = await Db.ScanPrefixAsync(Keys.MultipartUploadGlobalPrefix, 1024, afterKey, cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                afterKey = row.Key;
                var upload = Deserialize<XlMultipartUploadRecord>(row.Value);
                if (upload.InitiatedAt >= olderThanUtc)
                {
                    continue;
                }

                var parts = await ReadMultipartPartsAsync(upload.BucketName, upload.Key, upload.UploadId, 0, int.MaxValue, cancellationToken);
                var mutations = new List<LogDbMutation> { new(row.Key, null, true) };
                mutations.AddRange(parts.Select(part =>
                    new LogDbMutation(Keys.MultipartPart(upload.BucketName, upload.Key, upload.UploadId, part.PartNumber), null, true)));
                await Db.PutBatchAsync(mutations, cancellationToken);
                foreach (var part in parts)
                {
                    DeleteMultipartPartFilesQuietly(part);
                }

                deleted++;
            }
        }

        return deleted;
    }

    private async Task<XlMultipartUploadRecord?> ReadMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        return await Db.GetJsonAsync<XlMultipartUploadRecord>(Keys.MultipartUpload(bucketName, key, uploadId), cancellationToken);
    }

    private async Task<IReadOnlyList<XlMultipartPartRecord>> ReadMultipartPartsAsync(
        string bucketName,
        string key,
        string uploadId,
        int partNumberMarker,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await Db.ScanPrefixAsync(
            Keys.MultipartPartPrefix(bucketName, key, uploadId),
            Math.Clamp(limit, 1, 100_000),
            partNumberMarker <= 0 ? null : Keys.MultipartPart(bucketName, key, uploadId, partNumberMarker),
            cancellationToken);
        return rows.Select(row => Deserialize<XlMultipartPartRecord>(row.Value))
            .ToArray();
    }

    private static string? MultipartUploadScanAfterKey(string bucketName, string? keyMarker, string? uploadIdMarker)
    {
        if (string.IsNullOrWhiteSpace(keyMarker))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(uploadIdMarker)
            ? Keys.MultipartUploadPrefix(bucketName) + Escape(keyMarker) + ":\uffff"
            : Keys.MultipartUpload(bucketName, keyMarker, uploadIdMarker);
    }

    private async Task<Stream> OpenReadableMultipartPartAsync(XlMultipartPartRecord part, CancellationToken cancellationToken)
    {
        foreach (var shard in part.Shards.OrderBy(shard => shard.SetIndex))
        {
            var opened = await TryOpenReadableShardAsync(shard, cancellationToken);
            if (opened.Stream is not null)
            {
                return opened.Stream;
            }
        }

        throw new MeansException(MeansErrorCodes.InvalidPart, "One or more uploaded part files are missing.", 400);
    }

    private void DeleteMultipartPartFilesQuietly(XlMultipartPartRecord part)
    {
        foreach (var shard in part.Shards)
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is not null)
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
            }
        }
    }

    private static MultipartUploadInfo ToMultipartUploadInfo(XlMultipartUploadRecord record)
    {
        return new MultipartUploadInfo(
            record.BucketName,
            record.Key,
            record.UploadId,
            record.ContentType,
            record.InitiatedAt,
            record.Metadata,
            record.CacheControl,
            record.ContentDisposition);
    }

    private static MultipartPartInfo ToMultipartPartInfo(XlMultipartPartRecord record)
    {
        return new MultipartPartInfo(
            record.BucketName,
            record.Key,
            record.UploadId,
            record.PartNumber,
            record.PartId,
            record.ETag,
            record.Size,
            record.LastModified);
    }

    private static string MultipartPartRelativePath(string bucketName, string uploadId, string partId, int setIndex)
    {
        return Path.Combine("objects", BucketHash(bucketName), "multipart-" + uploadId, partId, "part." + setIndex.ToString("D2"));
    }

    private static void ValidatePartNumber(int partNumber)
    {
        if (partNumber is < 1 or > 10000)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Part number must be between 1 and 10000.", 400);
        }
    }

    private static void ValidateCompleteParts(IReadOnlyList<CompletedMultipartPart> parts)
    {
        if (parts.Count == 0)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "CompleteMultipartUpload requires at least one part.", 400);
        }

        var previous = 0;
        foreach (var part in parts)
        {
            ValidatePartNumber(part.PartNumber);
            if (part.PartNumber <= previous)
            {
                throw new MeansException(MeansErrorCodes.InvalidPartOrder, "Parts must be specified in ascending order.", 400);
            }

            if (string.IsNullOrWhiteSpace(NormalizeEtag(part.ETag)))
            {
                throw new MeansException(MeansErrorCodes.MalformedXML, "Each completed part must include an ETag.", 400);
            }

            previous = part.PartNumber;
        }
    }

    private static byte[] HexToBytes(string hex) => Convert.FromHexString(hex);

    private static MeansException NoSuchUpload()
    {
        return new MeansException(MeansErrorCodes.NoSuchUpload, "The specified multipart upload does not exist.", 404);
    }
}
