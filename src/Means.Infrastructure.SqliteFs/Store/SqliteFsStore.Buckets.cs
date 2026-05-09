using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, created_utc from buckets order by name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buckets = new List<BucketInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new BucketInfo(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1))));
        }

        return buckets;
    }

    public async Task<BucketInfo> CreateBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into buckets(name, created_utc) values($name, $created);";
        command.Parameters.AddWithValue("$name", bucketName);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // SQLite constraint 19 maps to the S3-style "bucket already exists" conflict.
            throw new MeansException(MeansErrorCodes.BucketAlreadyExists, "Bucket already exists.", 409);
        }

        return new BucketInfo(bucketName, createdAt);
    }

    public async Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, created_utc from buckets where name = $name;";
        command.Parameters.AddWithValue("$name", bucketName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BucketInfo(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)))
            : null;
    }

    public async Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = (SqliteTransaction)transaction;
            countCommand.CommandText = """
                select
                    (select count(*) from objects where bucket_name = $bucket)
                    + (select count(*) from multipart_uploads where bucket_name = $bucket);
                """;
            countCommand.Parameters.AddWithValue("$bucket", bucketName);
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (count > 0)
            {
                throw new MeansException(MeansErrorCodes.BucketNotEmpty, "Bucket is not empty.", 409);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "delete from bucket_policies where bucket_name = $bucket; delete from buckets where name = $bucket;";
            command.Parameters.AddWithValue("$bucket", bucketName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
