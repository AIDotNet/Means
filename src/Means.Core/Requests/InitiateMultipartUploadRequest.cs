namespace Means.Core;

/// <summary>
/// Storage-layer command for starting a multipart upload.
/// Object metadata is captured at initiation, matching S3 multipart semantics.
/// </summary>
public sealed record InitiateMultipartUploadRequest(
    string BucketName,
    string Key,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    string? CacheControl,
    string? ContentDisposition);
