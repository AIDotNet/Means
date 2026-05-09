namespace Means.Core;

/// <summary>
/// Part reference supplied by CompleteMultipartUpload.
/// </summary>
public sealed record CompletedMultipartPart(int PartNumber, string ETag);
