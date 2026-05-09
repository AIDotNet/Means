using System.Text.Json;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private static readonly JsonSerializerOptions BucketSettingsJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BucketSettings> GetBucketSettingsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        return await GetBucketSettingsAsync(connection, bucketName, cancellationToken);
    }

    public async Task SaveBucketSettingsAsync(BucketSettings settings, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, settings.BucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into bucket_settings(bucket_name, default_response_headers_json, default_metadata_json, updated_utc)
            values($bucket, $headers, $metadata, $updated)
            on conflict(bucket_name) do update set
                default_response_headers_json = excluded.default_response_headers_json,
                default_metadata_json = excluded.default_metadata_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$bucket", settings.BucketName);
        command.Parameters.AddWithValue("$headers", JsonSerializer.Serialize(settings.DefaultResponseHeaders, BucketSettingsJsonOptions));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(settings.DefaultMetadata, BucketSettingsJsonOptions));
        command.Parameters.AddWithValue("$updated", (settings.UpdatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<BucketSettings> GetBucketSettingsAsync(
        SqliteConnection connection,
        string bucketName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select default_response_headers_json, default_metadata_json, updated_utc
            from bucket_settings
            where bucket_name = $bucket;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new BucketSettings(
                bucketName,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                UpdatedAt: null);
        }

        return new BucketSettings(
            bucketName,
            DeserializeDictionary(reader.GetString(0)),
            DeserializeDictionary(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2)));
    }

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string json)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json, BucketSettingsJsonOptions)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase);
    }
}
