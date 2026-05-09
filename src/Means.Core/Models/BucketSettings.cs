namespace Means.Core;

/// <summary>
/// Bucket-scoped console settings applied by the S3 response layer.
/// These settings describe response defaults; object metadata still takes precedence.
/// </summary>
public sealed record BucketSettings(
    string BucketName,
    IReadOnlyDictionary<string, string> DefaultResponseHeaders,
    IReadOnlyDictionary<string, string> DefaultMetadata,
    DateTimeOffset? UpdatedAt);
