using Means.Core;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<string?> GetPolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select policy_json from bucket_policies where bucket_name = $bucket;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task PutPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into bucket_policies(bucket_name, policy_json, updated_utc)
            values($bucket, $policy, $updated)
            on conflict(bucket_name) do update set policy_json = excluded.policy_json, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$policy", policyJson);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeletePolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from bucket_policies where bucket_name = $bucket;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
