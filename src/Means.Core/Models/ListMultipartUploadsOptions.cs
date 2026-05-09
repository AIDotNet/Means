namespace Means.Core;

/// <summary>
/// Options for listing in-progress multipart uploads in a bucket.
/// </summary>
public sealed record ListMultipartUploadsOptions(
    string? Prefix,
    string? KeyMarker,
    string? UploadIdMarker,
    int MaxUploads);
