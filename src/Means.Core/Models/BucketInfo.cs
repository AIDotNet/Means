namespace Means.Core;

/// <summary>
/// Persistent bucket descriptor. Buckets are the top-level namespace for objects.
/// </summary>
public sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);
