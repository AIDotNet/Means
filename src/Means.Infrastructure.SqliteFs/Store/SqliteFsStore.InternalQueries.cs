using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    /// <summary>
    /// Shared bucket existence check used before object and policy operations.
    /// Keeping this in one helper gives all callers the same S3-compatible NoSuchBucket behavior.
    /// </summary>
    private async Task<bool> BucketExistsAsync(SqliteConnection connection, string bucketName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select 1 from buckets where name = $bucket;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<string?> GetObjectIdAsync(SqliteConnection connection, string bucketName, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select object_id from objects where bucket_name = $bucket and key = $key;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<ObjectInfo?> GetObjectInfoAsync(SqliteConnection connection, string bucketName, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, etag, content_length, content_type, last_modified_utc, cache_control, content_disposition
            from objects
            where bucket_name = $bucket and key = $key;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        string objectId;
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

            objectId = reader.GetString(0);
            etag = reader.GetString(1);
            length = reader.GetInt64(2);
            contentType = reader.GetString(3);
            lastModified = DateTimeOffset.Parse(reader.GetString(4));
            cacheControl = reader.IsDBNull(5) ? null : reader.GetString(5);
            contentDisposition = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        var metadata = await GetMetadataAsync(connection, bucketName, key, cancellationToken);
        return new ObjectInfo(bucketName, key, objectId, etag, length, contentType, lastModified, metadata, cacheControl, contentDisposition);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(SqliteConnection connection, string bucketName, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, value from object_metadata where bucket_name = $bucket and key = $key order by name;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }

    private static string? PrefixUpperBound(string prefix)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        var chars = prefix.ToCharArray();
        for (var index = chars.Length - 1; index >= 0; index--)
        {
            if (chars[index] == char.MaxValue)
            {
                continue;
            }

            chars[index]++;
            return new string(chars, 0, index + 1);
        }

        return null;
    }

    /// <summary>
    /// Writes object metadata and user metadata as one SQLite transaction unit.
    /// The caller owns the blob-file move and passes the same transaction so the object becomes
    /// visible only after both rows and metadata have been committed.
    /// </summary>
    private static async Task UpsertObjectAsync(SqliteConnection connection, SqliteTransaction transaction, ObjectInfo info, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into objects(bucket_name, key, object_id, etag, content_length, content_type, last_modified_utc, cache_control, content_disposition)
                values($bucket, $key, $objectId, $etag, $length, $contentType, $lastModified, $cacheControl, $contentDisposition)
                on conflict(bucket_name, key) do update set
                    object_id = excluded.object_id,
                    etag = excluded.etag,
                    content_length = excluded.content_length,
                    content_type = excluded.content_type,
                    last_modified_utc = excluded.last_modified_utc,
                    cache_control = excluded.cache_control,
                    content_disposition = excluded.content_disposition;
                """;
            command.Parameters.AddWithValue("$bucket", info.BucketName);
            command.Parameters.AddWithValue("$key", info.Key);
            command.Parameters.AddWithValue("$objectId", info.ObjectId);
            command.Parameters.AddWithValue("$etag", info.ETag);
            command.Parameters.AddWithValue("$length", info.ContentLength);
            command.Parameters.AddWithValue("$contentType", info.ContentType);
            command.Parameters.AddWithValue("$lastModified", info.LastModified.ToString("O"));
            command.Parameters.AddWithValue("$cacheControl", (object?)info.CacheControl ?? DBNull.Value);
            command.Parameters.AddWithValue("$contentDisposition", (object?)info.ContentDisposition ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMetadata = connection.CreateCommand())
        {
            deleteMetadata.Transaction = transaction;
            deleteMetadata.CommandText = "delete from object_metadata where bucket_name = $bucket and key = $key;";
            deleteMetadata.Parameters.AddWithValue("$bucket", info.BucketName);
            deleteMetadata.Parameters.AddWithValue("$key", info.Key);
            await deleteMetadata.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in info.Metadata)
        {
            await using var metadataCommand = connection.CreateCommand();
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText = """
                insert into object_metadata(bucket_name, key, name, value)
                values($bucket, $key, $name, $value);
                """;
            metadataCommand.Parameters.AddWithValue("$bucket", info.BucketName);
            metadataCommand.Parameters.AddWithValue("$key", info.Key);
            metadataCommand.Parameters.AddWithValue("$name", item.Key);
            metadataCommand.Parameters.AddWithValue("$value", item.Value);
            await metadataCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
