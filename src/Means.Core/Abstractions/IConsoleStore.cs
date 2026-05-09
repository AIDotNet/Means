namespace Means.Core;

/// <summary>
/// Administrative read/write boundary used by the built-in web console.
/// It intentionally stays separate from the S3 data-plane abstractions so browser-facing
/// management features do not leak storage implementation details or signing secrets.
/// </summary>
public interface IConsoleStore
{
    Task<ConsoleStorageMetrics> GetStorageMetricsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BucketUsageInfo>> ListBucketUsageAsync(CancellationToken cancellationToken);

    Task<BucketConsoleSummary> GetBucketSummaryAsync(
        string bucketName,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken);

    Task<BucketSettings> GetBucketSettingsAsync(string bucketName, CancellationToken cancellationToken);

    Task SaveBucketSettingsAsync(BucketSettings settings, CancellationToken cancellationToken);

    Task RecordRequestMetricAsync(ConsoleRequestMetric metric, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConsoleHourlyMetric>> ListHourlyMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConsoleBucketActivity>> ListBucketActivityAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken cancellationToken);

    Task<SystemSettings?> GetSystemSettingsAsync(CancellationToken cancellationToken);

    Task SaveSystemSettingsAsync(SystemSettings settings, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccessKeyInfo>> ListAccessKeysAsync(CancellationToken cancellationToken);

    Task<AccessKeySecretResult> CreateAccessKeyAsync(string? accessKey, CancellationToken cancellationToken);

    Task DeleteAccessKeyAsync(string accessKey, CancellationToken cancellationToken);

    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken);
}
