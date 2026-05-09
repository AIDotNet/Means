namespace Means;

/// <summary>
/// Configuration for <see cref="MeansClient"/>.
/// </summary>
public sealed class MeansClientOptions
{
    /// <summary>
    /// Creates options for a Means endpoint.
    /// </summary>
    public MeansClientOptions()
    {
    }

    /// <summary>
    /// Creates options for a Means endpoint.
    /// </summary>
    public MeansClientOptions(Uri endpoint, MeansCredentials? credentials = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Credentials = credentials;
    }

    /// <summary>
    /// Base service endpoint, for example <c>https://api.means.local</c>.
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://localhost:5000");

    /// <summary>
    /// Access key credentials used for SigV4 signing. Leave null for anonymous requests.
    /// </summary>
    public MeansCredentials? Credentials { get; set; }

    /// <summary>
    /// SigV4 region. Means defaults to a single S3-compatible region.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// SigV4 service name.
    /// </summary>
    public string Service { get; set; } = "s3";

    /// <summary>
    /// Uses path-style addressing (<c>/bucket/key</c>) when true and virtual-hosted-style addressing
    /// (<c>bucket.host/key</c>) when false.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// DNS suffix used for virtual-hosted-style requests. When null and the endpoint host starts with
    /// <c>api.</c>, the suffix is inferred by removing that prefix.
    /// </summary>
    public string? VirtualHostedDomainSuffix { get; set; }

    /// <summary>
    /// Optional timeout applied when the client owns the underlying <see cref="HttpClient"/>.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    internal MeansClientOptions Clone()
    {
        return new MeansClientOptions
        {
            Endpoint = Endpoint,
            Credentials = Credentials,
            Region = Region,
            Service = Service,
            ForcePathStyle = ForcePathStyle,
            VirtualHostedDomainSuffix = VirtualHostedDomainSuffix,
            Timeout = Timeout
        };
    }

    internal void Validate()
    {
        if (Endpoint is null)
        {
            throw new InvalidOperationException($"{nameof(Endpoint)} is required.");
        }

        if (!Endpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"{nameof(Endpoint)} must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new InvalidOperationException($"{nameof(Region)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Service))
        {
            throw new InvalidOperationException($"{nameof(Service)} is required.");
        }
    }
}
