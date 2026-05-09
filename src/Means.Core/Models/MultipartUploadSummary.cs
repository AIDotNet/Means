namespace Means.Core;

/// <summary>
/// Bucket-level summary for one in-progress multipart upload.
/// </summary>
public sealed record MultipartUploadSummary(
    string Key,
    string UploadId,
    DateTimeOffset InitiatedAt);
