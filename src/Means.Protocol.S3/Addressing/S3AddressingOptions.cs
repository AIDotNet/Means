namespace Means.Protocol.S3;

/// <summary>
/// Hostname rules used before a request enters authentication or authorization.
/// Means supports both S3 addressing styles: path-style for local compatibility and
/// virtual-hosted-style for production bucket subdomains.
/// </summary>
public sealed class S3AddressingOptions
{
    /// <summary>
    /// Canonical path-style host, for example https://api.means.local/{bucket}/{key}.
    /// Requests to this host resolve the bucket from the first path segment.
    /// </summary>
    public string ServiceHost { get; set; } = "api.means.local";

    /// <summary>
    /// Domain suffix used for virtual-hosted-style buckets, for example
    /// https://{bucket}.means.local/{key}.
    /// </summary>
    public string DomainSuffix { get; set; } = "means.local";

    /// <summary>
    /// Same-origin transport alias used by the built-in console.
    /// Browser uploads/downloads can call /s3/{bucket}/{key} without conflicting with SPA routes.
    /// </summary>
    public string AliasPrefix { get; set; } = "/s3";
}
