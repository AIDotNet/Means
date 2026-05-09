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
        var maxKeys = options.MaxKeys <= 0 ? 1000 : Math.Min(options.MaxKeys, 1000);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select key, etag, content_length, last_modified_utc, content_type
            from objects
            where bucket_name = $bucket and key like $prefix
              and ($token is null or key > $token)
            order by key
            limit $limit;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$prefix", prefix + "%");
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
        var finalPath = GetObjectPath(objectId);
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

        // Visibility is controlled by the metadata transaction: bytes are moved into their final
        // opaque path before commit, then the object becomes readable only after SQLite commits.
        File.Move(tempPath, finalPath, overwrite: false);
        string? previousObjectId = null;
        var info = new ObjectInfo(
            request.BucketName,
            request.Key,
            objectId,
            etag,
            length,
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            DateTimeOffset.UtcNow,
            NormalizeMetadata(request.Metadata),
            request.CacheControl,
            request.ContentDisposition);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            previousObjectId = await GetObjectIdAsync(connection, request.BucketName, request.Key, cancellationToken);
            await UpsertObjectAsync(connection, (SqliteTransaction)transaction, info, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteFileQuietly(finalPath);
            throw;
        }

        if (!string.IsNullOrEmpty(previousObjectId) && previousObjectId != objectId)
        {
            DeleteFileQuietly(GetObjectPath(previousObjectId));
        }

        return info;
    }

    public async Task<ObjectData> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var info = await HeadObjectAsync(bucketName, key, cancellationToken);
        var path = GetObjectPath(info.ObjectId);
        if (!File.Exists(path))
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is missing.", 404);
        }

        return new ObjectData(info, File.OpenRead(path));
    }

    public async Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var info = await GetObjectInfoAsync(connection, bucketName, key, cancellationToken);
        if (info is null)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
        }

        return info;
    }

    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var objectId = await GetObjectIdAsync(connection, bucketName, key, cancellationToken);
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

        await transaction.CommitAsync(cancellationToken);
        if (!string.IsNullOrEmpty(objectId))
        {
            DeleteFileQuietly(GetObjectPath(objectId));
        }
    }

    public async Task<ObjectInfo> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken)
    {
        // CopyObject is implemented as a server-side read/write within the same adapter.
        // The object stream never returns to the client, matching the intended S3 behavior.
        await using var source = await GetObjectAsync(request.SourceBucket, request.SourceKey, cancellationToken);
        var metadata = request.Metadata.Count > 0 ? request.Metadata : source.Info.Metadata;
        return await PutObjectAsync(
            new PutObjectRequest(
                request.DestinationBucket,
                request.DestinationKey,
                source.Content,
                source.Info.ContentType,
                metadata,
                request.CacheControl ?? source.Info.CacheControl,
                request.ContentDisposition ?? source.Info.ContentDisposition),
            cancellationToken);
    }
}
