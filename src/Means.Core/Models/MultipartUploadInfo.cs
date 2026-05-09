namespace Means.Core;

/// <summary>
/// Metadata for an in-progress multipart upload.
/// </summary>
public sealed record MultipartUploadInfo(
    string BucketName,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedAt,
    IReadOnlyDictionary<string, string> Metadata,
    string? CacheControl,
    string? ContentDisposition);
