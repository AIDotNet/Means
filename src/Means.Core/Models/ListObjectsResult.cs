namespace Means.Core;

/// <summary>
/// Storage-neutral result used by the protocol layer to render S3 ListBucketResult XML.
/// </summary>
public sealed record ListObjectsResult(
    string BucketName,
    string? Prefix,
    string? Delimiter,
    int KeyCount,
    bool IsTruncated,
    string? NextContinuationToken,
    IReadOnlyList<ListedObject> Objects,
    IReadOnlyList<string> CommonPrefixes);
