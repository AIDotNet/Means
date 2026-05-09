namespace Means.Core;

/// <summary>
/// Storage-layer command for assembling a multipart upload into the final object.
/// </summary>
public sealed record CompleteMultipartUploadRequest(
    string BucketName,
    string Key,
    string UploadId,
    IReadOnlyList<CompletedMultipartPart> Parts,
    string? IdempotencyKey = null);
