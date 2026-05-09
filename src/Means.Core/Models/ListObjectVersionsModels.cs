namespace Means.Core;

public sealed record ListObjectVersionsOptions(
    string? Prefix,
    string? Delimiter,
    string? KeyMarker,
    string? VersionIdMarker,
    int MaxKeys);

public sealed record ListedObjectVersion(
    string Key,
    string VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    string ETag,
    long Size,
    DateTimeOffset LastModified);

public sealed record ListObjectVersionsResult(
    string BucketName,
    string? Prefix,
    string? Delimiter,
    string? KeyMarker,
    string? VersionIdMarker,
    int MaxKeys,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextVersionIdMarker,
    IReadOnlyList<ListedObjectVersion> Versions,
    IReadOnlyList<string> CommonPrefixes);
