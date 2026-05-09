namespace Means.Core;

/// <summary>
/// Result returned by bucket-level ListMultipartUploads.
/// </summary>
public sealed record ListMultipartUploadsResult(
    string BucketName,
    string? Prefix,
    string? Delimiter,
    string? KeyMarker,
    string? UploadIdMarker,
    int MaxUploads,
    bool IsTruncated,
    string? NextKeyMarker,
    string? NextUploadIdMarker,
    IReadOnlyList<MultipartUploadSummary> Uploads,
    IReadOnlyList<string> CommonPrefixes);
