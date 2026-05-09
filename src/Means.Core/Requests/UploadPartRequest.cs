namespace Means.Core;

/// <summary>
/// Storage-layer command for writing one multipart upload part.
/// </summary>
public sealed record UploadPartRequest(
    string BucketName,
    string Key,
    string UploadId,
    int PartNumber,
    Stream Content);
