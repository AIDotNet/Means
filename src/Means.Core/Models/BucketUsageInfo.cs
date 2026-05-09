namespace Means.Core;

/// <summary>
/// Bucket summary enriched with object count and byte usage for console tables and dashboards.
/// </summary>
public sealed record BucketUsageInfo(
    string BucketName,
    DateTimeOffset CreatedAt,
    long ObjectCount,
    long TotalBytes);
