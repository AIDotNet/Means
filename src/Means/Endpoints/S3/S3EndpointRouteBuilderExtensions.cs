namespace Means.Endpoints.S3;

/// <summary>
/// Route registration for the S3-compatible data plane.
/// S3 uses method + host + path/query semantics rather than many MVC-style routes, so one catch-all
/// route is the clearest entry point before address resolution decides bucket/object handling.
/// </summary>
public static class S3EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMeansS3DataPlane(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var serviceHost = configuration["Means:S3:ServiceHost"] ?? "api.means.local";
        var domainSuffix = configuration["Means:S3:DomainSuffix"] ?? "means.local";
        endpoints.MapMethods("/s3/{**path}", ["GET", "PUT", "POST", "HEAD", "DELETE", "OPTIONS"], S3Endpoint.HandleAsync);
        endpoints.MapMethods("/{**path}", ["GET", "PUT", "POST", "HEAD", "DELETE", "OPTIONS"], S3Endpoint.HandleAsync)
            .RequireHost(serviceHost, "*." + domainSuffix);
        return endpoints;
    }
}
