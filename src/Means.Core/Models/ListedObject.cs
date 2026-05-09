namespace Means.Core;

/// <summary>
/// Minimal object projection used by ListObjectsV2 responses.
/// </summary>
public sealed record ListedObject(
    string Key,
    string ETag,
    long Size,
    DateTimeOffset LastModified,
    string ContentType);
