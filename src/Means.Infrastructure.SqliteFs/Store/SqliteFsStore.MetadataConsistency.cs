using System.Security.Cryptography;
using System.Text;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string MetadataCommitStatusPending = "Pending";
    private const string MetadataCommitStatusCommitted = "Committed";
    private const string MetadataCommitOperationPutObject = "PutObject";
    private const string MetadataCommitOperationCompleteMultipart = "CompleteMultipartUpload";

    private static async Task<DateTimeOffset> NextMetadataTimestampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string clockId = "object-metadata";
        DateTimeOffset? previous = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "select last_utc from metadata_clock where clock_id = $clockId;";
            select.Parameters.AddWithValue("$clockId", clockId);
            var value = (string?)await select.ExecuteScalarAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(value))
            {
                previous = DateTimeOffset.Parse(value).ToUniversalTime();
            }
        }

        var now = DateTimeOffset.UtcNow;
        var next = previous is null || now > previous.Value ? now : previous.Value.AddTicks(1);
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                insert into metadata_clock(clock_id, last_utc)
                values($clockId, $last)
                on conflict(clock_id) do update set
                    last_utc = excluded.last_utc;
                """;
            upsert.Parameters.AddWithValue("$clockId", clockId);
            upsert.Parameters.AddWithValue("$last", next.ToString("O"));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        return next;
    }

    private static async Task InsertMetadataCommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commitId,
        string operation,
        string bucketName,
        string key,
        string objectId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into metadata_commits(
                commit_id,
                operation,
                bucket_name,
                key,
                object_id,
                status,
                created_utc,
                committed_utc,
                error)
            values(
                $commitId,
                $operation,
                $bucket,
                $key,
                $objectId,
                $status,
                $created,
                null,
                null);
            """;
        command.Parameters.AddWithValue("$commitId", commitId);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$status", MetadataCommitStatusPending);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CommitMetadataCommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commitId,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update metadata_commits
            set status = $status,
                committed_utc = $committed,
                error = null
            where commit_id = $commitId;
            """;
        command.Parameters.AddWithValue("$status", MetadataCommitStatusCommitted);
        command.Parameters.AddWithValue("$committed", committedAt.ToString("O"));
        command.Parameters.AddWithValue("$commitId", commitId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var normalized = idempotencyKey.Trim();
        if (normalized.Length > 256)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Idempotency key must be 256 characters or fewer.", 400);
        }

        return normalized;
    }

    private static string ComputePutObjectRequestHash(
        PutObjectRequest request,
        string etag,
        long contentLength)
    {
        var builder = new StringBuilder();
        builder.Append(MetadataCommitOperationPutObject).Append('\n');
        builder.Append(request.BucketName).Append('\n');
        builder.Append(request.Key).Append('\n');
        builder.Append(etag).Append('\n');
        builder.Append(contentLength).Append('\n');
        builder.Append(string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType).Append('\n');
        builder.Append(request.CacheControl ?? "").Append('\n');
        builder.Append(request.ContentDisposition ?? "").Append('\n');
        AppendMetadataHashInput(builder, request.Metadata);
        return Sha256Hex(builder.ToString());
    }

    private static string ComputeCompleteMultipartRequestHash(CompleteMultipartUploadRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(MetadataCommitOperationCompleteMultipart).Append('\n');
        builder.Append(request.BucketName).Append('\n');
        builder.Append(request.Key).Append('\n');
        builder.Append(request.UploadId).Append('\n');
        foreach (var part in request.Parts.OrderBy(part => part.PartNumber))
        {
            builder.Append(part.PartNumber).Append(':').Append(part.ETag.Trim().Trim('"').ToLowerInvariant()).Append('\n');
        }

        return Sha256Hex(builder.ToString());
    }

    private static void AppendMetadataHashInput(StringBuilder builder, IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var item in metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(item.Key.ToLowerInvariant()).Append('=').Append(item.Value).Append('\n');
        }
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private async Task<ObjectInfo?> TryReadIdempotentObjectAsync(
        SqliteConnection connection,
        string operation,
        string bucketName,
        string key,
        string? idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select operation, bucket_name, key, request_hash, object_id
            from idempotency_records
            where idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", normalizedKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var existingOperation = reader.GetString(0);
        var existingBucket = reader.GetString(1);
        var existingKey = reader.GetString(2);
        var existingHash = reader.GetString(3);
        var objectId = reader.GetString(4);
        if (!string.Equals(existingOperation, operation, StringComparison.Ordinal)
            || !string.Equals(existingBucket, bucketName, StringComparison.Ordinal)
            || !string.Equals(existingKey, key, StringComparison.Ordinal)
            || !string.Equals(existingHash, requestHash, StringComparison.Ordinal))
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Idempotency key was already used for a different request.", 409);
        }

        return await GetObjectInfoByObjectIdAsync(connection, objectId, cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.InvalidRequest, "Idempotent operation result is no longer available.", 409);
    }

    private static async Task InsertIdempotencyRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operation,
        string bucketName,
        string key,
        string? idempotencyKey,
        string requestHash,
        string objectId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        if (normalizedKey is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into idempotency_records(
                idempotency_key,
                operation,
                bucket_name,
                key,
                request_hash,
                object_id,
                created_utc)
            values(
                $idempotencyKey,
                $operation,
                $bucket,
                $key,
                $requestHash,
                $objectId,
                $created);
            """;
        command.Parameters.AddWithValue("$idempotencyKey", normalizedKey);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertObjectVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObjectInfo info,
        bool isDeleteMarker,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var versionId = info.ObjectId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into object_versions(
                    version_id,
                    bucket_name,
                    key,
                    object_id,
                    etag,
                    content_length,
                    content_type,
                    last_modified_utc,
                    cache_control,
                    content_disposition,
                    is_delete_marker,
                    created_utc)
                values(
                    $versionId,
                    $bucket,
                    $key,
                    $objectId,
                    $etag,
                    $length,
                    $contentType,
                    $lastModified,
                    $cacheControl,
                    $contentDisposition,
                    $deleteMarker,
                    $created);
                """;
            command.Parameters.AddWithValue("$versionId", versionId);
            command.Parameters.AddWithValue("$bucket", info.BucketName);
            command.Parameters.AddWithValue("$key", info.Key);
            command.Parameters.AddWithValue("$objectId", info.ObjectId);
            command.Parameters.AddWithValue("$etag", info.ETag);
            command.Parameters.AddWithValue("$length", info.ContentLength);
            command.Parameters.AddWithValue("$contentType", info.ContentType);
            command.Parameters.AddWithValue("$lastModified", info.LastModified.ToString("O"));
            command.Parameters.AddWithValue("$cacheControl", (object?)info.CacheControl ?? DBNull.Value);
            command.Parameters.AddWithValue("$contentDisposition", (object?)info.ContentDisposition ?? DBNull.Value);
            command.Parameters.AddWithValue("$deleteMarker", isDeleteMarker ? 1 : 0);
            command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in info.Metadata)
        {
            await using var metadataCommand = connection.CreateCommand();
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText = """
                insert into object_version_metadata(version_id, name, value)
                values($versionId, $name, $value);
                """;
            metadataCommand.Parameters.AddWithValue("$versionId", versionId);
            metadataCommand.Parameters.AddWithValue("$name", item.Key);
            metadataCommand.Parameters.AddWithValue("$value", item.Value);
            await metadataCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<ObjectInfo?> GetObjectInfoByObjectIdAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select bucket_name, key, etag, content_length, content_type, last_modified_utc, cache_control, content_disposition
            from object_versions
            where object_id = $objectId and is_delete_marker = 0
            order by created_utc desc
            limit 1;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);

        string bucketName;
        string key;
        string etag;
        long length;
        string contentType;
        DateTimeOffset lastModified;
        string? cacheControl;
        string? contentDisposition;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            bucketName = reader.GetString(0);
            key = reader.GetString(1);
            etag = reader.GetString(2);
            length = reader.GetInt64(3);
            contentType = reader.GetString(4);
            lastModified = DateTimeOffset.Parse(reader.GetString(5));
            cacheControl = reader.IsDBNull(6) ? null : reader.GetString(6);
            contentDisposition = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        var metadata = await GetObjectVersionMetadataAsync(connection, objectId, cancellationToken);
        return new ObjectInfo(bucketName, key, objectId, etag, length, contentType, lastModified, metadata, cacheControl, contentDisposition);
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetObjectVersionMetadataAsync(
        SqliteConnection connection,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, value from object_version_metadata where version_id = $versionId order by name;";
        command.Parameters.AddWithValue("$versionId", versionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }
}
