namespace Means.Protocol.S3;

/// <summary>
/// Normalized bucket/key address extracted from the incoming HTTP host and path.
/// Protocol handlers use this value so downstream code does not care whether the
/// original request was path-style or virtual-hosted-style.
/// </summary>
public sealed record S3Address(string? BucketName, string? ObjectKey, bool IsVirtualHostedStyle);
