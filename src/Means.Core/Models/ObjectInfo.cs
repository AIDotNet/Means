namespace Means.Core;

/// <summary>
/// Stored object metadata returned by head/read/write operations.
/// ObjectId is intentionally opaque so object keys never map directly to file-system paths.
/// </summary>
public sealed record ObjectInfo(
    string BucketName,
    string Key,
    string ObjectId,
    string ETag,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModified,
    IReadOnlyDictionary<string, string> Metadata,
    string? CacheControl,
    string? ContentDisposition);
