using Means.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Means.Middleware;

public static class RequestBodyLimitMiddleware
{
    public static IApplicationBuilder UseMeansRequestBodyLimits(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPut(context.Request.Method))
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    var settings = await context.RequestServices
                        .GetRequiredService<SystemSettingsService>()
                        .GetAsync(context.RequestAborted);
                    feature.MaxRequestBodySize = settings.MaxUploadSizeBytes;
                }
            }

            await next();
        });
    }
}
