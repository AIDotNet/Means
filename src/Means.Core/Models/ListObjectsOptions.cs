namespace Means.Core;

/// <summary>
/// S3 ListObjectsV2 options after query-string parsing.
/// </summary>
public sealed record ListObjectsOptions(
    string? Prefix,
    string? Delimiter,
    string? ContinuationToken,
    int MaxKeys);
