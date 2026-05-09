namespace Means.Core;

/// <summary>
/// Storage-layer command for server-side object copy.
/// The destination can override metadata while reusing source content.
/// </summary>
public sealed record CopyObjectRequest(
    string SourceBucket,
    string SourceKey,
    string? SourceVersionId,
    string DestinationBucket,
    string DestinationKey,
    IReadOnlyDictionary<string, string> Metadata,
    string MetadataDirective,
    string? ContentType,
    string? CacheControl,
    string? ContentDisposition);
