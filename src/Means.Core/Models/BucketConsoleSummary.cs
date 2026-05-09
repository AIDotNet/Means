namespace Means.Core;

/// <summary>
/// Bucket detail metrics for the console, combining stored usage and recent S3 activity.
/// </summary>
public sealed record BucketConsoleSummary(
    string BucketName,
    DateTimeOffset CreatedAt,
    long ObjectCount,
    long TotalBytes,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    long PutCount,
    long GetCount,
    long DeleteCount,
    long HeadCount,
    long ListCount,
    DateTimeOffset? LastActivityAt);
