using System.Security.Cryptography;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<ListObjectsResult> ListObjectsAsync(string bucketName, ListObjectsOptions options, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var prefix = options.Prefix ?? "";
        var prefixEnd = PrefixUpperBound(prefix);
        var maxKeys = options.MaxKeys <= 0 ? 1000 : Math.Min(options.MaxKeys, 1000);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select key, etag, content_length, last_modified_utc, content_type
            from objects
            where bucket_name = $bucket
              and key >= $prefix
              and ($prefixEnd is null or key < $prefixEnd)
              and ($token is null or key > $token)
            order by key
            limit $limit;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$prefixEnd", (object?)prefixEnd ?? DBNull.Value);
        command.Parameters.AddWithValue("$token", (object?)options.ContinuationToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maxKeys + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<ListedObject>();
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? nextToken = null;
        var scanned = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            scanned++;
            var key = reader.GetString(0);
            nextToken = key;
            if (scanned > maxKeys)
            {
                break;
            }

            // Delimiter emulates the S3 "folder" view by folding deeper keys into CommonPrefixes.
            if (!string.IsNullOrEmpty(options.Delimiter))
            {
                var rest = key[prefix.Length..];
                var delimiterIndex = rest.IndexOf(options.Delimiter, StringComparison.Ordinal);
                if (delimiterIndex >= 0)
                {
                    prefixes.Add(prefix + rest[..(delimiterIndex + options.Delimiter.Length)]);
                    continue;
                }
            }

            objects.Add(new ListedObject(
                key,
                reader.GetString(1),
                reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4)));
        }

        var isTruncated = scanned > maxKeys;
        return new ListObjectsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            objects.Count + prefixes.Count,
            isTruncated,
            isTruncated ? nextToken : null,
            objects,
            prefixes.ToArray());
    }

    public async Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, request.BucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        Directory.CreateDirectory(_options.ObjectsPath);
        Directory.CreateDirectory(Path.Combine(_options.ObjectsPath, "tmp"));
        var objectId = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_options.ObjectsPath, "tmp", objectId + ".tmp");

        string etag;
        string checksumSha256;
        long length = 0;
        try
        {
            await using (var output = File.Create(tempPath))
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = await request.Content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    md5.AppendData(buffer.AsSpan(0, read));
                    sha256.AppendData(buffer.AsSpan(0, read));
                    length += read;
                }

                etag = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
                checksumSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }

        var normalizedMetadata = NormalizeMetadata(request.Metadata);
        var normalizedRequest = request with { Metadata = normalizedMetadata };
        var requestHash = ComputePutObjectRequestHash(normalizedRequest, etag, length);
        var idempotentResult = await TryReadIdempotentObjectAsync(
            connection,
            MetadataCommitOperationPutObject,
            request.BucketName,
            request.Key,
            request.IdempotencyKey,
            requestHash,
            cancellationToken);
        if (idempotentResult is not null)
        {
            DeleteFileQuietly(tempPath);
            return idempotentResult;
        }

        // Visibility is controlled by the metadata transaction: bytes are moved into their final
        // opaque path before commit, then the object becomes readable only after SQLite commits.
        IReadOnlyList<ObjectReplicaRecord> replicas;
        try
        {
            replicas = await WriteObjectReplicasAsync(request.BucketName, request.Key, objectId, length, tempPath, checksumSha256, cancellationToken);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }

        ObjectEcWriteSet? ecWriteSet = null;
        try
        {
            ecWriteSet = await WriteObjectErasureCodingAsync(connection, request.BucketName, request.Key, objectId, length, tempPath, cancellationToken);
        }
        catch
        {
            DeleteReplicaFilesQuietly(replicas);
            DeleteFileQuietly(tempPath);
            throw;
        }

        string? previousObjectId = null;
        IReadOnlyList<string> previousReplicaPaths = Array.Empty<string>();
        IReadOnlyList<string> previousEcShardPaths = Array.Empty<string>();
        var preservePreviousVersion = await IsBucketVersioningEnabledAsync(connection, request.BucketName, cancellationToken);
        ObjectInfo info;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var commitAt = await NextMetadataTimestampAsync(connection, (SqliteTransaction)transaction, cancellationToken);
            info = new ObjectInfo(
                request.BucketName,
                request.Key,
                objectId,
                etag,
                length,
                string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
                commitAt,
                normalizedMetadata,
                request.CacheControl,
                request.ContentDisposition);
            var commitId = Guid.NewGuid().ToString("N");
            await InsertMetadataCommitAsync(
                connection,
                (SqliteTransaction)transaction,
                commitId,
                MetadataCommitOperationPutObject,
                request.BucketName,
                request.Key,
                objectId,
                commitAt,
                cancellationToken);

            previousObjectId = await GetObjectIdAsync(connection, request.BucketName, request.Key, cancellationToken);
            if (!preservePreviousVersion && !string.IsNullOrEmpty(previousObjectId) && previousObjectId != objectId)
            {
                previousReplicaPaths = await GetObjectReplicaPathsAsync(connection, previousObjectId, cancellationToken);
                previousEcShardPaths = await GetObjectEcShardPathsAsync(connection, previousObjectId, cancellationToken);
                await DeleteObjectReplicaRowsAsync(connection, (SqliteTransaction)transaction, previousObjectId, cancellationToken);
                await DeleteReplicaRepairRowsAsync(connection, (SqliteTransaction)transaction, previousObjectId, cancellationToken);
                await DeleteObjectErasureCodingRowsAsync(connection, (SqliteTransaction)transaction, previousObjectId, cancellationToken);
            }

            await UpsertObjectAsync(connection, (SqliteTransaction)transaction, info, cancellationToken);
            await ReplaceObjectReplicasAsync(connection, (SqliteTransaction)transaction, objectId, replicas, cancellationToken);
            await ReplaceObjectErasureCodingAsync(connection, (SqliteTransaction)transaction, ecWriteSet, cancellationToken);
            await InsertObjectVersionAsync(connection, (SqliteTransaction)transaction, info, isDeleteMarker: false, commitAt, cancellationToken);
            await InsertIdempotencyRecordAsync(
                connection,
                (SqliteTransaction)transaction,
                MetadataCommitOperationPutObject,
                request.BucketName,
                request.Key,
                request.IdempotencyKey,
                requestHash,
                objectId,
                commitAt,
                cancellationToken);
            await CommitMetadataCommitAsync(connection, (SqliteTransaction)transaction, commitId, commitAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteReplicaFilesQuietly(replicas);
            if (ecWriteSet is not null)
            {
                DeleteEcShardFilesQuietly(ecWriteSet.Shards);
            }

            DeleteFileQuietly(tempPath);
            throw;
        }

        DeleteFileQuietly(tempPath);
        if (!preservePreviousVersion && !string.IsNullOrEmpty(previousObjectId) && previousObjectId != objectId)
        {
            DeleteObjectFilesQuietly(previousObjectId, previousReplicaPaths);
            DeleteEcShardFilesQuietly(previousEcShardPaths);
        }

        return info;
    }

    public async Task<ObjectData> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        return await GetObjectAsync(bucketName, key, versionId: null, cancellationToken);
    }

    public async Task<ObjectData> GetObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var info = await HeadObjectAsync(bucketName, key, versionId, cancellationToken);
        if (IsHotObjectCacheEligible(info))
        {
            return await OpenCachedObjectAsync(info, cancellationToken);
        }

        var content = await OpenObjectContentAsync(info.ObjectId, cancellationToken);
        return new ObjectData(info, content.Content, content.ContentPath);
    }

    public async Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        return await HeadObjectAsync(bucketName, key, versionId: null, cancellationToken);
    }

    public async Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var info = string.IsNullOrWhiteSpace(versionId)
            ? await GetObjectInfoAsync(connection, bucketName, key, cancellationToken)
            : await GetObjectVersionInfoAsync(connection, bucketName, key, versionId, cancellationToken);
        if (info is null)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
        }

        return info;
    }

    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        _ = await DeleteObjectAsync(bucketName, key, versionId: null, cancellationToken);
    }

    public async Task<DeleteObjectResult> DeleteObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            return await DeleteObjectVersionAsync(connection, bucketName, key, versionId, cancellationToken);
        }

        if (await IsBucketVersioningEnabledAsync(connection, bucketName, cancellationToken))
        {
            var deleteMarkerVersionId = Guid.NewGuid().ToString("N");
            var createdAt = DateTimeOffset.UtcNow;
            await using var versionedDelete = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var deleted = await DeleteCurrentObjectRowsAsync(connection, (SqliteTransaction)versionedDelete, bucketName, key, cancellationToken);
                await InsertDeleteMarkerVersionAsync(
                    connection,
                    (SqliteTransaction)versionedDelete,
                    bucketName,
                    key,
                    deleteMarkerVersionId,
                    createdAt,
                    cancellationToken);
                await versionedDelete.CommitAsync(cancellationToken);
                return new DeleteObjectResult(bucketName, key, deleteMarkerVersionId, DeleteMarker: true);
            }
            catch
            {
                await versionedDelete.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var objectId = await GetObjectIdAsync(connection, bucketName, key, cancellationToken);
        var replicaPaths = string.IsNullOrEmpty(objectId)
            ? Array.Empty<string>()
            : await GetObjectReplicaPathsAsync(connection, objectId, cancellationToken);
        var ecShardPaths = string.IsNullOrEmpty(objectId)
            ? Array.Empty<string>()
            : await GetObjectEcShardPathsAsync(connection, objectId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                delete from object_metadata where bucket_name = $bucket and key = $key;
                delete from objects where bucket_name = $bucket and key = $key;
                """;
            command.Parameters.AddWithValue("$bucket", bucketName);
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrEmpty(objectId))
        {
            await DeleteObjectReplicaRowsAsync(connection, (SqliteTransaction)transaction, objectId, cancellationToken);
            await DeleteReplicaRepairRowsAsync(connection, (SqliteTransaction)transaction, objectId, cancellationToken);
            await DeleteObjectErasureCodingRowsAsync(connection, (SqliteTransaction)transaction, objectId, cancellationToken);
            await DeleteObjectVersionRowsForObjectAsync(connection, (SqliteTransaction)transaction, bucketName, key, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (!string.IsNullOrEmpty(objectId))
        {
            DeleteObjectFilesQuietly(objectId, replicaPaths);
            DeleteEcShardFilesQuietly(ecShardPaths);
        }

        return new DeleteObjectResult(bucketName, key, VersionId: null, DeleteMarker: false);
    }

    public async Task<ObjectInfo> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken)
    {
        // CopyObject is implemented as a server-side read/write within the same adapter.
        // The object stream never returns to the client, matching the intended S3 behavior.
        await using var source = await GetObjectAsync(request.SourceBucket, request.SourceKey, request.SourceVersionId, cancellationToken);
        var replaceMetadata = string.Equals(request.MetadataDirective, CopyMetadataDirectives.Replace, StringComparison.OrdinalIgnoreCase);
        var metadata = replaceMetadata ? request.Metadata : source.Info.Metadata;
        return await PutObjectAsync(
            new PutObjectRequest(
                request.DestinationBucket,
                request.DestinationKey,
                source.Content,
                string.IsNullOrWhiteSpace(request.ContentType) ? source.Info.ContentType : request.ContentType,
                metadata,
                replaceMetadata ? request.CacheControl : request.CacheControl ?? source.Info.CacheControl,
                replaceMetadata ? request.ContentDisposition : request.ContentDisposition ?? source.Info.ContentDisposition),
            cancellationToken);
    }
}
