namespace Means.Core;

/// <summary>
/// Storage-layer command for writing a complete object atomically.
/// Content remains a stream so HTTP uploads do not need to be fully buffered by the host.
/// </summary>
public sealed record PutObjectRequest(
    string BucketName,
    string Key,
    Stream Content,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    string? CacheControl,
    string? ContentDisposition);
