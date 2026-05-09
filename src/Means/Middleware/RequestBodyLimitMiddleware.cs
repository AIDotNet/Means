using Means.Core;
using Means.Endpoints.S3;
using Means.Protocol.S3;
using Means.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Means.Middleware;

public static class RequestBodyLimitMiddleware
{
    public static IApplicationBuilder UseMeansRequestBodyLimits(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var addressing = context.RequestServices.GetRequiredService<IOptions<S3AddressingOptions>>().Value;
            if (!IsS3UploadRequest(context, addressing))
            {
                await next();
                return;
            }

            var limiter = context.RequestServices.GetRequiredService<UploadConcurrencyLimiter>();
            await using var lease = await limiter.TryAcquireAsync(context.RequestAborted);
            if (lease is null)
            {
                var requestId = S3RequestIds.New();
                await S3ResponseWriter.WriteErrorAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    MeansErrorCodes.SlowDown,
                    "Too many concurrent upload requests.",
                    requestId,
                    new Dictionary<string, string> { ["Retry-After"] = "1" },
                    context.RequestAborted);
                return;
            }

            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                var settings = await context.RequestServices
                    .GetRequiredService<SystemSettingsService>()
                    .GetAsync(context.RequestAborted);
                feature.MaxRequestBodySize = settings.MaxUploadSizeBytes;
            }

            await next();
        });
    }

    private static bool IsS3UploadRequest(HttpContext context, S3AddressingOptions addressing)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            return false;
        }

        if (context.Request.Path.StartsWithSegments(addressing.AliasPrefix))
        {
            return true;
        }

        var host = context.Request.Host.Host;
        return string.Equals(host, addressing.ServiceHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + addressing.DomainSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
