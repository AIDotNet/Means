using System.Security.Cryptography;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const long MinimumMultipartPartSize = 5L * 1024 * 1024;

    public async Task<MultipartUploadInfo> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, request.BucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var upload = new MultipartUploadInfo(
            request.BucketName,
            request.Key,
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            DateTimeOffset.UtcNow,
            NormalizeMetadata(request.Metadata),
            request.CacheControl,
            request.ContentDisposition);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                insert into multipart_uploads(upload_id, bucket_name, key, content_type, cache_control, content_disposition, initiated_utc)
                values($uploadId, $bucket, $key, $contentType, $cacheControl, $contentDisposition, $initiated);
                """;
            command.Parameters.AddWithValue("$uploadId", upload.UploadId);
            command.Parameters.AddWithValue("$bucket", upload.BucketName);
            command.Parameters.AddWithValue("$key", upload.Key);
            command.Parameters.AddWithValue("$contentType", upload.ContentType);
            command.Parameters.AddWithValue("$cacheControl", (object?)upload.CacheControl ?? DBNull.Value);
            command.Parameters.AddWithValue("$contentDisposition", (object?)upload.ContentDisposition ?? DBNull.Value);
            command.Parameters.AddWithValue("$initiated", upload.InitiatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in upload.Metadata)
        {
            await using var metadataCommand = connection.CreateCommand();
            metadataCommand.Transaction = (SqliteTransaction)transaction;
            metadataCommand.CommandText = """
                insert into multipart_upload_metadata(upload_id, name, value)
                values($uploadId, $name, $value);
                """;
            metadataCommand.Parameters.AddWithValue("$uploadId", upload.UploadId);
            metadataCommand.Parameters.AddWithValue("$name", item.Key);
            metadataCommand.Parameters.AddWithValue("$value", item.Value);
            await metadataCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return upload;
    }

    public async Task<MultipartPartInfo> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidatePartNumber(request.PartNumber);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, request.BucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var upload = await GetMultipartUploadAsync(connection, request.BucketName, request.Key, request.UploadId, cancellationToken)
            ?? throw NoSuchUpload();

        Directory.CreateDirectory(_options.ObjectsPath);
        Directory.CreateDirectory(Path.Combine(_options.ObjectsPath, "tmp"));
        var partId = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_options.ObjectsPath, "tmp", partId + ".part.tmp");
        var finalPath = GetMultipartPartPath(partId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        string etag;
        long length = 0;
        try
        {
            await using (var output = File.Create(tempPath))
            {
                using var md5 = MD5.Create();
                await using var crypto = new CryptoStream(output, md5, CryptoStreamMode.Write);
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = await request.Content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await crypto.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    length += read;
                }

                await crypto.FlushAsync(cancellationToken);
                crypto.FlushFinalBlock();
                etag = Convert.ToHexString(md5.Hash ?? []).ToLowerInvariant();
            }
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }

        File.Move(tempPath, finalPath, overwrite: false);
        string? previousPartId = null;
        var part = new MultipartPartInfo(
            upload.BucketName,
            upload.Key,
            upload.UploadId,
            request.PartNumber,
            partId,
            etag,
            length,
            DateTimeOffset.UtcNow);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            previousPartId = await GetMultipartPartIdAsync(connection, request.UploadId, request.PartNumber, cancellationToken);
            await UpsertMultipartPartAsync(connection, (SqliteTransaction)transaction, part, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteFileQuietly(finalPath);
            throw;
        }

        if (!string.IsNullOrEmpty(previousPartId) && previousPartId != partId)
        {
            DeleteFileQuietly(GetMultipartPartPath(previousPartId));
        }

        return part;
    }

    public async Task<ObjectInfo> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        ValidateCompleteParts(request.Parts);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, request.BucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var requestHash = ComputeCompleteMultipartRequestHash(request);
        var idempotentResult = await TryReadIdempotentObjectAsync(
            connection,
            MetadataCommitOperationCompleteMultipart,
            request.BucketName,
            request.Key,
            request.IdempotencyKey,
            requestHash,
            cancellationToken);
        if (idempotentResult is not null)
        {
            return idempotentResult;
        }

        var upload = await GetMultipartUploadAsync(connection, request.BucketName, request.Key, request.UploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var storedParts = (await ReadMultipartPartsAsync(connection, request.UploadId, cancellationToken))
            .ToDictionary(part => part.PartNumber);

        var orderedParts = new List<MultipartPartInfo>(request.Parts.Count);
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

            orderedParts.Add(stored);
        }

        Directory.CreateDirectory(_options.ObjectsPath);
        Directory.CreateDirectory(Path.Combine(_options.ObjectsPath, "tmp"));
        var objectId = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_options.ObjectsPath, "tmp", objectId + ".multipart.tmp");

        var length = 0L;
        string etag;
        string checksumSha256;
        try
        {
            await using (var output = File.Create(tempPath))
            {
                using var multipartMd5 = MD5.Create();
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                foreach (var part in orderedParts)
                {
                    multipartMd5.TransformBlock(HexToBytes(part.ETag), 0, 16, null, 0);
                    var partPath = GetMultipartPartPath(part.PartId);
                    if (!File.Exists(partPath))
                    {
                        throw new MeansException(MeansErrorCodes.InvalidPart, "One or more uploaded part files are missing.", 400);
                    }

                    await using var input = File.OpenRead(partPath);
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha256.AppendData(buffer.AsSpan(0, read));
                    }

                    length += part.Size;
                }

                multipartMd5.TransformFinalBlock([], 0, 0);
                etag = Convert.ToHexString(multipartMd5.Hash ?? []).ToLowerInvariant() + "-" + orderedParts.Count;
                checksumSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }

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
        var obsoletePartIds = storedParts.Values.Select(part => part.PartId).ToArray();
        ObjectInfo info;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var commitAt = await NextMetadataTimestampAsync(connection, (SqliteTransaction)transaction, cancellationToken);
            info = new ObjectInfo(
                upload.BucketName,
                upload.Key,
                objectId,
                etag,
                length,
                upload.ContentType,
                commitAt,
                upload.Metadata,
                upload.CacheControl,
                upload.ContentDisposition);
            var commitId = Guid.NewGuid().ToString("N");
            await InsertMetadataCommitAsync(
                connection,
                (SqliteTransaction)transaction,
                commitId,
                MetadataCommitOperationCompleteMultipart,
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
                MetadataCommitOperationCompleteMultipart,
                request.BucketName,
                request.Key,
                request.IdempotencyKey,
                requestHash,
                objectId,
                commitAt,
                cancellationToken);
            await DeleteMultipartUploadRowsAsync(connection, (SqliteTransaction)transaction, request.UploadId, cancellationToken);
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
        foreach (var partId in obsoletePartIds)
        {
            DeleteFileQuietly(GetMultipartPartPath(partId));
        }

        if (!preservePreviousVersion && !string.IsNullOrEmpty(previousObjectId) && previousObjectId != objectId)
        {
            DeleteObjectFilesQuietly(previousObjectId, previousReplicaPaths);
            DeleteEcShardFilesQuietly(previousEcShardPaths);
        }

        return info;
    }

    public async Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var upload = await GetMultipartUploadAsync(connection, bucketName, key, uploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var parts = await ReadMultipartPartsAsync(connection, upload.UploadId, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DeleteMultipartUploadRowsAsync(connection, (SqliteTransaction)transaction, upload.UploadId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var part in parts)
        {
            DeleteFileQuietly(GetMultipartPartPath(part.PartId));
        }
    }

    public async Task<ListPartsResult> ListPartsAsync(string bucketName, string key, string uploadId, int partNumberMarker, int maxParts, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var upload = await GetMultipartUploadAsync(connection, bucketName, key, uploadId, cancellationToken)
            ?? throw NoSuchUpload();
        var effectiveMaxParts = maxParts <= 0 ? 1000 : Math.Min(maxParts, 1000);
        var parts = await ReadMultipartPartsAsync(connection, upload.UploadId, Math.Max(0, partNumberMarker), effectiveMaxParts + 1, cancellationToken);
        var isTruncated = parts.Count > effectiveMaxParts;
        var visibleParts = parts.Take(effectiveMaxParts).ToArray();
        var nextMarker = isTruncated && visibleParts.Length > 0 ? visibleParts[^1].PartNumber : 0;
        return new ListPartsResult(upload.BucketName, upload.Key, upload.UploadId, upload.InitiatedAt, partNumberMarker, nextMarker, effectiveMaxParts, isTruncated, visibleParts);
    }

    public async Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucketName, ListMultipartUploadsOptions options, CancellationToken cancellationToken)
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
        var delimiter = options.Delimiter ?? "";
        var maxUploads = options.MaxUploads <= 0 ? 1000 : Math.Min(options.MaxUploads, 1000);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select key, upload_id, initiated_utc
            from multipart_uploads
            where bucket_name = $bucket
              and key >= $prefix
              and ($prefixEnd is null or key < $prefixEnd)
              and (
                  $keyMarker is null
                  or key > $keyMarker
                  or (key = $keyMarker and ($uploadIdMarker is null or upload_id > $uploadIdMarker))
              )
            order by key, upload_id
            limit $limit;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$prefixEnd", (object?)prefixEnd ?? DBNull.Value);
        command.Parameters.AddWithValue("$keyMarker", (object?)options.KeyMarker ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploadIdMarker", (object?)options.UploadIdMarker ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maxUploads + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var uploads = new List<MultipartUploadSummary>();
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        string? nextKeyMarker = null;
        string? nextUploadIdMarker = null;
        var scanned = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            scanned++;
            var key = reader.GetString(0);
            var uploadId = reader.GetString(1);
            nextKeyMarker = key;
            nextUploadIdMarker = uploadId;
            if (scanned > maxUploads)
            {
                break;
            }

            if (!string.IsNullOrEmpty(delimiter))
            {
                var rest = key[prefix.Length..];
                var delimiterIndex = rest.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimiterIndex >= 0)
                {
                    prefixes.Add(prefix + rest[..(delimiterIndex + delimiter.Length)]);
                    continue;
                }
            }

            uploads.Add(new MultipartUploadSummary(
                key,
                uploadId,
                DateTimeOffset.Parse(reader.GetString(2))));
        }

        var isTruncated = scanned > maxUploads;
        return new ListMultipartUploadsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            options.KeyMarker,
            options.UploadIdMarker,
            maxUploads,
            isTruncated,
            isTruncated ? nextKeyMarker : null,
            isTruncated ? nextUploadIdMarker : null,
            uploads,
            prefixes.ToArray());
    }

    public async Task<int> CleanupMultipartUploadsAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var uploads = new List<string>();
        var partIds = new List<string>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = """
                select u.upload_id, p.part_id
                from multipart_uploads u
                left join multipart_parts p on p.upload_id = u.upload_id
                where u.initiated_utc < $cutoff
                order by u.upload_id;
                """;
            selectCommand.Parameters.AddWithValue("$cutoff", olderThanUtc.ToUniversalTime().ToString("O"));
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var uploadId = reader.GetString(0);
                if (uploads.Count == 0 || uploads[^1] != uploadId)
                {
                    uploads.Add(uploadId);
                }

                if (!reader.IsDBNull(1))
                {
                    partIds.Add(reader.GetString(1));
                }
            }
        }

        if (uploads.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "delete from multipart_uploads where initiated_utc < $cutoff;";
            deleteCommand.Parameters.AddWithValue("$cutoff", olderThanUtc.ToUniversalTime().ToString("O"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var partId in partIds)
        {
            DeleteFileQuietly(GetMultipartPartPath(partId));
        }

        return uploads.Count;
    }

    private async Task<MultipartUploadInfo?> GetMultipartUploadAsync(SqliteConnection connection, string bucketName, string key, string uploadId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select content_type, cache_control, content_disposition, initiated_utc
            from multipart_uploads
            where upload_id = $uploadId and bucket_name = $bucket and key = $key;
            """;
        command.Parameters.AddWithValue("$uploadId", uploadId);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);

        string contentType;
        string? cacheControl;
        string? contentDisposition;
        DateTimeOffset initiatedAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            contentType = reader.GetString(0);
            cacheControl = reader.IsDBNull(1) ? null : reader.GetString(1);
            contentDisposition = reader.IsDBNull(2) ? null : reader.GetString(2);
            initiatedAt = DateTimeOffset.Parse(reader.GetString(3));
        }

        var metadata = await GetMultipartMetadataAsync(connection, uploadId, cancellationToken);
        return new MultipartUploadInfo(bucketName, key, uploadId, contentType, initiatedAt, metadata, cacheControl, contentDisposition);
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetMultipartMetadataAsync(SqliteConnection connection, string uploadId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, value from multipart_upload_metadata where upload_id = $uploadId order by name;";
        command.Parameters.AddWithValue("$uploadId", uploadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }

    private async Task<IReadOnlyList<MultipartPartInfo>> ReadMultipartPartsAsync(SqliteConnection connection, string uploadId, CancellationToken cancellationToken)
    {
        return await ReadMultipartPartsAsync(connection, uploadId, partNumberMarker: 0, limit: int.MaxValue, cancellationToken);
    }

    private async Task<IReadOnlyList<MultipartPartInfo>> ReadMultipartPartsAsync(SqliteConnection connection, string uploadId, int partNumberMarker, int limit, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select u.bucket_name, u.key, p.part_number, p.part_id, p.etag, p.content_length, p.last_modified_utc
            from multipart_parts p
            join multipart_uploads u on u.upload_id = p.upload_id
            where p.upload_id = $uploadId
              and p.part_number > $partNumberMarker
            order by p.part_number
            limit $limit;
            """;
        command.Parameters.AddWithValue("$uploadId", uploadId);
        command.Parameters.AddWithValue("$partNumberMarker", partNumberMarker);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var parts = new List<MultipartPartInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            parts.Add(new MultipartPartInfo(
                reader.GetString(0),
                reader.GetString(1),
                uploadId,
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return parts;
    }

    private static async Task<string?> GetMultipartPartIdAsync(SqliteConnection connection, string uploadId, int partNumber, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select part_id from multipart_parts where upload_id = $uploadId and part_number = $partNumber;";
        command.Parameters.AddWithValue("$uploadId", uploadId);
        command.Parameters.AddWithValue("$partNumber", partNumber);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task UpsertMultipartPartAsync(SqliteConnection connection, SqliteTransaction transaction, MultipartPartInfo part, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into multipart_parts(upload_id, part_number, part_id, etag, content_length, last_modified_utc)
            values($uploadId, $partNumber, $partId, $etag, $length, $lastModified)
            on conflict(upload_id, part_number) do update set
                part_id = excluded.part_id,
                etag = excluded.etag,
                content_length = excluded.content_length,
                last_modified_utc = excluded.last_modified_utc;
            """;
        command.Parameters.AddWithValue("$uploadId", part.UploadId);
        command.Parameters.AddWithValue("$partNumber", part.PartNumber);
        command.Parameters.AddWithValue("$partId", part.PartId);
        command.Parameters.AddWithValue("$etag", part.ETag);
        command.Parameters.AddWithValue("$length", part.Size);
        command.Parameters.AddWithValue("$lastModified", part.LastModified.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteMultipartUploadRowsAsync(SqliteConnection connection, SqliteTransaction transaction, string uploadId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from multipart_uploads where upload_id = $uploadId;";
        command.Parameters.AddWithValue("$uploadId", uploadId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string NormalizeEtag(string etag)
    {
        return etag.Trim().Trim('"').ToLowerInvariant();
    }

    private static byte[] HexToBytes(string hex)
    {
        return Convert.FromHexString(hex);
    }

    private static MeansException NoSuchUpload()
    {
        return new MeansException(MeansErrorCodes.NoSuchUpload, "The specified multipart upload does not exist.", 404);
    }
}
