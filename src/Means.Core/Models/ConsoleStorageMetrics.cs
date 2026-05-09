namespace Means.Core;

/// <summary>
/// Global single-node storage metrics used by the console overview page.
/// Future distributed metrics can extend this model without changing the S3 data-plane contract.
/// </summary>
public sealed record ConsoleStorageMetrics(long BucketCount, long ObjectCount, long TotalBytes);
