using System.Security.Cryptography;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task<AccessKeyCredential?> GetCredentialAsync(string accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var record = await Db.GetJsonAsync<XlAccessKeyRecord>(Keys.AccessKey(accessKey), cancellationToken);
        return record is null ? null : new AccessKeyCredential(record.AccessKey, record.SecretKey, record.Enabled);
    }

    public async Task<string?> GetPolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await Db.GetJsonAsync<string>(Keys.Policy(bucketName), cancellationToken);
    }

    public async Task PutPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutJsonAsync(Keys.Policy(bucketName), policyJson, cancellationToken);
    }

    public async Task DeletePolicyAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await Db.PutBatchAsync([new LogDbMutation(Keys.Policy(bucketName), null, true)], cancellationToken);
    }

    public async Task<ConsoleStorageMetrics> GetStorageMetricsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var buckets = await Db.ScanPrefixAsync(Keys.BucketPrefix, 100_000, null, cancellationToken);
        var objects = await CurrentObjectRecordsAsync(cancellationToken);
        return new ConsoleStorageMetrics(
            buckets.Count,
            objects.LongCount(record => !record.IsDeleteMarker),
            objects.Where(record => !record.IsDeleteMarker).Sum(record => record.ContentLength));
    }

    public async Task<IReadOnlyList<BucketUsageInfo>> ListBucketUsageAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var objects = await CurrentObjectRecordsAsync(cancellationToken);
        var usage = objects.Where(record => !record.IsDeleteMarker)
            .GroupBy(record => record.BucketName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (Count: group.LongCount(), Bytes: group.Sum(record => record.ContentLength)),
                StringComparer.Ordinal);
        return (await ListBucketsAsync(cancellationToken))
            .Select(bucket =>
            {
                usage.TryGetValue(bucket.Name, out var value);
                return new BucketUsageInfo(bucket.Name, bucket.CreatedAt, value.Count, value.Bytes);
            })
            .ToArray();
    }

    public async Task<BucketConsoleSummary> GetBucketSummaryAsync(
        string bucketName,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var bucket = await GetBucketAsync(bucketName, cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        var objects = (await CurrentObjectRecordsAsync(cancellationToken))
            .Where(record => string.Equals(record.BucketName, bucketName, StringComparison.Ordinal) && !record.IsDeleteMarker)
            .ToArray();
        var metrics = await ReadMetricAggregatesAsync(startUtc, endUtc, cancellationToken);
        var bucketMetrics = metrics
            .Where(metric => string.Equals(metric.BucketName, bucketName, StringComparison.Ordinal))
            .ToArray();
        return new BucketConsoleSummary(
            bucket.Name,
            bucket.CreatedAt,
            objects.LongLength,
            objects.Sum(record => record.ContentLength),
            bucketMetrics.Sum(metric => metric.RequestCount),
            bucketMetrics.Sum(metric => metric.ErrorCount),
            bucketMetrics.Sum(metric => metric.IngressBytes),
            bucketMetrics.Sum(metric => metric.EgressBytes),
            bucketMetrics.Sum(metric => metric.PutCount),
            bucketMetrics.Sum(metric => metric.GetCount),
            bucketMetrics.Sum(metric => metric.DeleteCount),
            bucketMetrics.Sum(metric => metric.HeadCount),
            bucketMetrics.Sum(metric => metric.ListCount),
            bucketMetrics.Length == 0 ? null : bucketMetrics.Max(metric => metric.LastActivityAt));
    }

    public async Task<ClusterDiagnostics> GetClusterDiagnosticsAsync(
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var topology = await GetClusterTopologyAsync(offlineBeforeUtc, cancellationToken);
        var objects = (await CurrentObjectRecordsAsync(cancellationToken))
            .Where(record => !record.IsDeleteMarker)
            .ToArray();
        long replicaRecords = 0;
        long existingReplicaFiles = 0;
        long missingReplicaFiles = 0;
        long underReplicated = 0;
        foreach (var record in objects)
        {
            var manifest = await TryReadManifestAsync(record, cancellationToken);
            if (manifest is null)
            {
                underReplicated++;
                continue;
            }

            var objectExisting = 0;
            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                replicaRecords++;
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (disk is not null && File.Exists(Path.Combine(disk.RootPath, shard.RelativePath)))
                {
                    existingReplicaFiles++;
                    objectExisting++;
                }
                else
                {
                    missingReplicaFiles++;
                }
            }

            if (objectExisting < WriteQuorum)
            {
                underReplicated++;
            }
        }

        var healRows = await Db.ScanPrefixAsync(Keys.HealPrefix, 100_000, null, cancellationToken);
        var diskCount = topology.Nodes.SelectMany(node => node.Disks).Count();
        var onlineDiskCount = topology.Nodes.SelectMany(node => node.Disks)
            .Count(disk => disk.Status == StorageDiskStatuses.Online);
        var totalCapacity = topology.Pools.Sum(pool => pool.TotalBytes);
        var availableCapacity = topology.Pools.Sum(pool => pool.AvailableBytes);
        var bucketCount = (await ListBucketsAsync(cancellationToken)).Count;
        var ecProfiles = await ListErasureCodingProfilesAsync(cancellationToken);
        return new ClusterDiagnostics(
            DateTimeOffset.UtcNow,
            new ClusterDiagnosticsSummary(
                bucketCount,
                objects.LongLength,
                objects.Sum(record => record.ContentLength),
                topology.Nodes.Count,
                topology.Nodes.Count(node => node.Status == ClusterNodeStatuses.Online),
                topology.Nodes.Count(node => node.Status != ClusterNodeStatuses.Online),
                topology.Pools.Count,
                diskCount,
                onlineDiskCount,
                diskCount - onlineDiskCount,
                totalCapacity,
                availableCapacity,
                Math.Max(0, totalCapacity - availableCapacity)),
            topology,
            new ObjectReplicaDiagnostics(
                WriteQuorum,
                objects.LongLength,
                replicaRecords,
                replicaRecords,
                existingReplicaFiles,
                missingReplicaFiles,
                underReplicated,
                underReplicated,
                0),
            new ReplicaRepairQueueDiagnostics(
                healRows.Count,
                healRows.Count,
                0,
                0,
                0,
                0,
                null,
                healRows.Count == 0 ? null : DateTimeOffset.UtcNow,
                [new ReplicaRepairQueueStatusDiagnostics("Pending", healRows.Count)]),
            new MetadataDiagnostics(0, 0),
            new ErasureCodingDiagnostics(
                ecProfiles.Count,
                ecProfiles.Count(profile => profile.Enabled),
                ecProfiles.Count(profile => !profile.Enabled)),
            []);
    }

    public async Task<BucketSettings> GetBucketSettingsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        return await Db.GetJsonAsync<BucketSettings>(Keys.BucketSettings(bucketName), cancellationToken)
            ?? new BucketSettings(
                bucketName,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null);
    }

    public async Task SaveBucketSettingsAsync(BucketSettings settings, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(settings.BucketName, cancellationToken);
        var saved = settings with { UpdatedAt = settings.UpdatedAt ?? DateTimeOffset.UtcNow };
        await Db.PutJsonAsync(Keys.BucketSettings(settings.BucketName), saved, cancellationToken);
    }

    public async Task RecordRequestMetricAsync(ConsoleRequestMetric metric, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var occurredAt = metric.OccurredAt.ToUniversalTime();
        var hourUtc = TruncateToHour(occurredAt);
        var bucketName = metric.BucketName ?? string.Empty;
        var method = metric.Method.ToUpperInvariant();
        var existing = await Db.GetJsonAsync<XlRequestMetricAggregate>(Keys.Metric(hourUtc, bucketName), cancellationToken);
        var updated = new XlRequestMetricAggregate(
            hourUtc,
            bucketName,
            (existing?.RequestCount ?? 0) + 1,
            (existing?.ErrorCount ?? 0) + (metric.IsError ? 1 : 0),
            (existing?.IngressBytes ?? 0) + Math.Max(0, metric.IngressBytes),
            (existing?.EgressBytes ?? 0) + Math.Max(0, metric.EgressBytes),
            (existing?.PutCount ?? 0) + (method == "PUT" && !metric.IsListOperation ? 1 : 0),
            (existing?.GetCount ?? 0) + (method == "GET" && !metric.IsListOperation ? 1 : 0),
            (existing?.DeleteCount ?? 0) + (method == "DELETE" && !metric.IsListOperation ? 1 : 0),
            (existing?.HeadCount ?? 0) + (method == "HEAD" && !metric.IsListOperation ? 1 : 0),
            (existing?.ListCount ?? 0) + (metric.IsListOperation ? 1 : 0),
            existing is null || occurredAt > existing.LastActivityAt ? occurredAt : existing.LastActivityAt);
        await Db.PutJsonAsync(Keys.Metric(hourUtc, bucketName), updated, cancellationToken);
    }

    public async Task<IReadOnlyList<ConsoleHourlyMetric>> ListHourlyMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var metrics = await ReadMetricAggregatesAsync(startUtc, endUtc, cancellationToken);
        return metrics.GroupBy(metric => metric.HourUtc)
            .OrderBy(group => group.Key)
            .Select(group => new ConsoleHourlyMetric(
                group.Key,
                group.Sum(metric => metric.RequestCount),
                group.Sum(metric => metric.ErrorCount),
                group.Sum(metric => metric.IngressBytes),
                group.Sum(metric => metric.EgressBytes),
                group.Sum(metric => metric.PutCount),
                group.Sum(metric => metric.GetCount),
                group.Sum(metric => metric.DeleteCount),
                group.Sum(metric => metric.HeadCount),
                group.Sum(metric => metric.ListCount)))
            .ToArray();
    }

    public async Task<IReadOnlyList<ConsoleBucketActivity>> ListBucketActivityAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var metrics = await ReadMetricAggregatesAsync(startUtc, endUtc, cancellationToken);
        return metrics.Where(metric => !string.IsNullOrEmpty(metric.BucketName))
            .GroupBy(metric => metric.BucketName, StringComparer.Ordinal)
            .Select(group => new ConsoleBucketActivity(
                group.Key,
                group.Sum(metric => metric.RequestCount),
                group.Sum(metric => metric.ErrorCount),
                group.Sum(metric => metric.IngressBytes),
                group.Sum(metric => metric.EgressBytes),
                group.Max(metric => metric.LastActivityAt)))
            .OrderByDescending(activity => activity.LastActivityAt)
            .ThenByDescending(activity => activity.RequestCount)
            .Take(Math.Clamp(limit, 1, 50))
            .ToArray();
    }

    public async Task<SystemSettings?> GetSystemSettingsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await Db.GetJsonAsync<SystemSettings>(Keys.SystemSettings, cancellationToken);
    }

    public async Task SaveSystemSettingsAsync(SystemSettings settings, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        _ = SystemSettings.ValidateMaxUploadSizeBytes(settings.MaxUploadSizeBytes);
        await Db.PutJsonAsync(Keys.SystemSettings, settings, cancellationToken);
    }

    public async Task<IReadOnlyList<AccessKeyInfo>> ListAccessKeysAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.AccessKeyPrefix, 100_000, null, cancellationToken);
        return rows.Select(row => Deserialize<XlAccessKeyRecord>(row.Value))
            .OrderByDescending(record => record.CreatedAt)
            .Select(record => new AccessKeyInfo(record.AccessKey, record.Enabled, record.CreatedAt))
            .ToArray();
    }

    public async Task<AccessKeySecretResult> CreateAccessKeyAsync(string? accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var key = string.IsNullOrWhiteSpace(accessKey) ? "means_" + RandomHex(10) : accessKey.Trim();
        if (await Db.GetAsync(Keys.AccessKey(key), cancellationToken) is not null)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Access key already exists.", 409);
        }

        var secret = RandomHex(32);
        var createdAt = DateTimeOffset.UtcNow;
        var record = new XlAccessKeyRecord(key, secret, true, createdAt);
        await Db.PutJsonAsync(Keys.AccessKey(key), record, cancellationToken);
        return new AccessKeySecretResult(key, secret, true, createdAt);
    }

    public async Task DeleteAccessKeyAsync(string accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (string.Equals(accessKey, _options.DefaultAccessKey, StringComparison.Ordinal))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "The bootstrap access key cannot be deleted from the console.", 400);
        }

        await Db.PutBatchAsync([new LogDbMutation(Keys.AccessKey(accessKey), null, true)], cancellationToken);
    }

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var id = entry.Id > 0 ? entry.Id : Math.Max(Interlocked.Increment(ref _auditId), DateTimeOffset.UtcNow.UtcTicks);
        await Db.PutJsonAsync(Keys.Audit(id), entry with { Id = id, OccurredAt = entry.OccurredAt.ToUniversalTime() }, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.AuditPrefix, 100_000, null, cancellationToken);
        return rows.Select(row => Deserialize<AuditEntry>(row.Value))
            .OrderByDescending(entry => entry.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();
    }

    private async Task<IReadOnlyList<XlObjectRecord>> CurrentObjectRecordsAsync(CancellationToken cancellationToken)
    {
        var rows = await Db.ScanPrefixAsync(Keys.CurrentObjectGlobalPrefix, 100_000, null, cancellationToken);
        return rows.Select(row => Deserialize<XlObjectRecord>(row.Value)).ToArray();
    }

    private async Task<IReadOnlyList<XlRequestMetricAggregate>> ReadMetricAggregatesAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.MetricPrefix, 100_000, null, cancellationToken);
        var start = startUtc.ToUniversalTime();
        var end = endUtc.ToUniversalTime();
        return rows.Select(row => Deserialize<XlRequestMetricAggregate>(row.Value))
            .Where(metric => metric.HourUtc >= start && metric.HourUtc < end)
            .ToArray();
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
