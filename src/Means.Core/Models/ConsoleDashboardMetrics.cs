namespace Means.Core;

/// <summary>
/// One S3 data-plane request as observed by the console metrics collector.
/// </summary>
public sealed record ConsoleRequestMetric(
    DateTimeOffset OccurredAt,
    string? BucketName,
    string Method,
    bool IsListOperation,
    bool IsError,
    long IngressBytes,
    long EgressBytes);

public sealed record ConsoleHourlyMetric(
    DateTimeOffset HourUtc,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    long PutCount,
    long GetCount,
    long DeleteCount,
    long HeadCount,
    long ListCount);

public sealed record ConsoleBucketActivity(
    string BucketName,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    DateTimeOffset LastActivityAt);
