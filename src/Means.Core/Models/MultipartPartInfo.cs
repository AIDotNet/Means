namespace Means.Core;

/// <summary>
/// Stored multipart part metadata.
/// PartId is opaque storage-internal state used to locate the part file.
/// </summary>
public sealed record MultipartPartInfo(
    string BucketName,
    string Key,
    string UploadId,
    int PartNumber,
    string PartId,
    string ETag,
    long Size,
    DateTimeOffset LastModified);
