namespace Means.Core;

/// <summary>
/// Result returned by ListParts for a single multipart upload.
/// </summary>
public sealed record ListPartsResult(
    string BucketName,
    string Key,
    string UploadId,
    DateTimeOffset InitiatedAt,
    int PartNumberMarker,
    int NextPartNumberMarker,
    int MaxParts,
    bool IsTruncated,
    IReadOnlyList<MultipartPartInfo> Parts);
