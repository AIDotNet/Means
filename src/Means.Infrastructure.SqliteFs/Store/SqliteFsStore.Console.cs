using System.Security.Cryptography;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<ConsoleStorageMetrics> GetStorageMetricsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                (select count(*) from buckets),
                (select count(*) from objects),
                coalesce((select sum(content_length) from objects), 0);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ConsoleStorageMetrics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<IReadOnlyList<BucketUsageInfo>> ListBucketUsageAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select b.name, b.created_utc, count(o.key), coalesce(sum(o.content_length), 0)
            from buckets b
            left join objects o on o.bucket_name = b.name
            group by b.name, b.created_utc
            order by b.name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buckets = new List<BucketUsageInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new BucketUsageInfo(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return buckets;
    }

    public async Task<BucketConsoleSummary> GetBucketSummaryAsync(
        string bucketName,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        string name;
        DateTimeOffset createdAt;
        long objectCount;
        long totalBytes;
        await using (var usageCommand = connection.CreateCommand())
        {
            usageCommand.CommandText = """
                select b.name, b.created_utc, count(o.key), coalesce(sum(o.content_length), 0)
                from buckets b
                left join objects o on o.bucket_name = b.name
                where b.name = $bucket
                group by b.name, b.created_utc;
                """;
            usageCommand.Parameters.AddWithValue("$bucket", bucketName);
            await using var usageReader = await usageCommand.ExecuteReaderAsync(cancellationToken);
            if (!await usageReader.ReadAsync(cancellationToken))
            {
                throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
            }

            name = usageReader.GetString(0);
            createdAt = DateTimeOffset.Parse(usageReader.GetString(1));
            objectCount = usageReader.GetInt64(2);
            totalBytes = usageReader.GetInt64(3);
        }

        await using var activityCommand = connection.CreateCommand();
        activityCommand.CommandText = """
            select
                coalesce(sum(request_count), 0),
                coalesce(sum(error_count), 0),
                coalesce(sum(ingress_bytes), 0),
                coalesce(sum(egress_bytes), 0),
                coalesce(sum(put_count), 0),
                coalesce(sum(get_count), 0),
                coalesce(sum(delete_count), 0),
                coalesce(sum(head_count), 0),
                coalesce(sum(list_count), 0),
                max(last_activity_utc)
            from request_hourly_metrics
            where bucket_name = $bucket and hour_utc >= $start and hour_utc < $end;
            """;
        activityCommand.Parameters.AddWithValue("$bucket", bucketName);
        activityCommand.Parameters.AddWithValue("$start", startUtc.ToUniversalTime().ToString("O"));
        activityCommand.Parameters.AddWithValue("$end", endUtc.ToUniversalTime().ToString("O"));
        await using var activityReader = await activityCommand.ExecuteReaderAsync(cancellationToken);
        await activityReader.ReadAsync(cancellationToken);

        return new BucketConsoleSummary(
            name,
            createdAt,
            objectCount,
            totalBytes,
            activityReader.GetInt64(0),
            activityReader.GetInt64(1),
            activityReader.GetInt64(2),
            activityReader.GetInt64(3),
            activityReader.GetInt64(4),
            activityReader.GetInt64(5),
            activityReader.GetInt64(6),
            activityReader.GetInt64(7),
            activityReader.GetInt64(8),
            activityReader.IsDBNull(9) ? null : DateTimeOffset.Parse(activityReader.GetString(9)));
    }

    public async Task RecordRequestMetricAsync(ConsoleRequestMetric metric, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var occurredAt = metric.OccurredAt.ToUniversalTime();
        var hourUtc = TruncateToHour(occurredAt);
        var bucketName = metric.BucketName ?? "";
        var method = metric.Method.ToUpperInvariant();
        var putCount = method == "PUT" && !metric.IsListOperation ? 1 : 0;
        var getCount = method == "GET" && !metric.IsListOperation ? 1 : 0;
        var deleteCount = method == "DELETE" && !metric.IsListOperation ? 1 : 0;
        var headCount = method == "HEAD" && !metric.IsListOperation ? 1 : 0;
        var listCount = metric.IsListOperation ? 1 : 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into request_hourly_metrics(
                hour_utc,
                bucket_name,
                request_count,
                error_count,
                ingress_bytes,
                egress_bytes,
                put_count,
                get_count,
                delete_count,
                head_count,
                list_count,
                last_activity_utc)
            values(
                $hour,
                $bucket,
                1,
                $error,
                $ingress,
                $egress,
                $put,
                $get,
                $delete,
                $head,
                $list,
                $occurred)
            on conflict(hour_utc, bucket_name) do update set
                request_count = request_count + excluded.request_count,
                error_count = error_count + excluded.error_count,
                ingress_bytes = ingress_bytes + excluded.ingress_bytes,
                egress_bytes = egress_bytes + excluded.egress_bytes,
                put_count = put_count + excluded.put_count,
                get_count = get_count + excluded.get_count,
                delete_count = delete_count + excluded.delete_count,
                head_count = head_count + excluded.head_count,
                list_count = list_count + excluded.list_count,
                last_activity_utc = max(last_activity_utc, excluded.last_activity_utc);
            """;
        command.Parameters.AddWithValue("$hour", hourUtc.ToString("O"));
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$error", metric.IsError ? 1 : 0);
        command.Parameters.AddWithValue("$ingress", Math.Max(0, metric.IngressBytes));
        command.Parameters.AddWithValue("$egress", Math.Max(0, metric.EgressBytes));
        command.Parameters.AddWithValue("$put", putCount);
        command.Parameters.AddWithValue("$get", getCount);
        command.Parameters.AddWithValue("$delete", deleteCount);
        command.Parameters.AddWithValue("$head", headCount);
        command.Parameters.AddWithValue("$list", listCount);
        command.Parameters.AddWithValue("$occurred", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConsoleHourlyMetric>> ListHourlyMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                hour_utc,
                coalesce(sum(request_count), 0),
                coalesce(sum(error_count), 0),
                coalesce(sum(ingress_bytes), 0),
                coalesce(sum(egress_bytes), 0),
                coalesce(sum(put_count), 0),
                coalesce(sum(get_count), 0),
                coalesce(sum(delete_count), 0),
                coalesce(sum(head_count), 0),
                coalesce(sum(list_count), 0)
            from request_hourly_metrics
            where hour_utc >= $start and hour_utc < $end
            group by hour_utc
            order by hour_utc;
            """;
        command.Parameters.AddWithValue("$start", startUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", endUtc.ToUniversalTime().ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var metrics = new List<ConsoleHourlyMetric>();
        while (await reader.ReadAsync(cancellationToken))
        {
            metrics.Add(new ConsoleHourlyMetric(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9)));
        }

        return metrics;
    }

    public async Task<IReadOnlyList<ConsoleBucketActivity>> ListBucketActivityAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                bucket_name,
                coalesce(sum(request_count), 0),
                coalesce(sum(error_count), 0),
                coalesce(sum(ingress_bytes), 0),
                coalesce(sum(egress_bytes), 0),
                max(last_activity_utc)
            from request_hourly_metrics
            where bucket_name <> '' and hour_utc >= $start and hour_utc < $end
            group by bucket_name
            order by max(last_activity_utc) desc, sum(request_count) desc
            limit $limit;
            """;
        command.Parameters.AddWithValue("$start", startUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", endUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buckets = new List<ConsoleBucketActivity>();
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new ConsoleBucketActivity(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return buckets;
    }

    public async Task<IReadOnlyList<AccessKeyInfo>> ListAccessKeysAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select access_key, enabled, created_utc from access_keys order by created_utc desc;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<AccessKeyInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new AccessKeyInfo(reader.GetString(0), reader.GetBoolean(1), DateTimeOffset.Parse(reader.GetString(2))));
        }

        return keys;
    }

    public async Task<AccessKeySecretResult> CreateAccessKeyAsync(string? accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        var key = string.IsNullOrWhiteSpace(accessKey) ? "means_" + RandomHex(10) : accessKey.Trim();
        var secret = RandomHex(32);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into access_keys(access_key, secret_key, enabled, created_utc)
            values($accessKey, $secretKey, 1, $created);
            """;
        command.Parameters.AddWithValue("$accessKey", key);
        command.Parameters.AddWithValue("$secretKey", secret);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Access key already exists.", 409);
        }

        return new AccessKeySecretResult(key, secret, Enabled: true, createdAt);
    }

    public async Task DeleteAccessKeyAsync(string accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (string.Equals(accessKey, _options.DefaultAccessKey, StringComparison.Ordinal))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "The bootstrap access key cannot be deleted from the console.", 400);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from access_keys where access_key = $accessKey;";
        command.Parameters.AddWithValue("$accessKey", accessKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into audit_entries(occurred_utc, actor, action, resource, status, message)
            values($occurred, $actor, $action, $resource, $status, $message);
            """;
        command.Parameters.AddWithValue("$occurred", entry.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$actor", entry.Actor);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$resource", entry.Resource);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$message", (object?)entry.Message ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, occurred_utc, actor, action, resource, status, message
            from audit_entries
            order by id desc
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }

    private static string RandomHex(int bytes)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
