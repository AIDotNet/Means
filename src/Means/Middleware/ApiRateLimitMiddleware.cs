using System.Globalization;
using Means.Configuration;
using Means.Core;
using Means.Endpoints.Console;
using Means.Endpoints.S3;
using Means.Protocol.S3;
using Means.Serialization;
using Means.Services;
using Microsoft.Extensions.Options;

namespace Means.Middleware;

public static class ApiRateLimitMiddleware
{
    public static IApplicationBuilder UseMeansApiRateLimits(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<MeansRateLimitOptions>>().Value;
            if (!limits.Enabled)
            {
                await next();
                return;
            }

            var addressing = context.RequestServices.GetRequiredService<IOptions<S3AddressingOptions>>().Value;
            var profile = ResolveProfile(context, limits, addressing);
            if (profile is null)
            {
                await next();
                return;
            }

            var store = context.RequestServices.GetRequiredService<ApiRateLimitStore>();
            var now = DateTimeOffset.UtcNow;
            var partitionKey = profile.Value.Name + ":" + ClientPartitionKey(context);
            if (store.TryAcquire(
                    partitionKey,
                    profile.Value.PermitLimit,
                    TimeSpan.FromSeconds(profile.Value.WindowSeconds),
                    now,
                    out var retryAfter))
            {
                await next();
                return;
            }

            await WriteRejectedAsync(context, addressing, retryAfter, context.RequestAborted);
        });
    }

    private static RateLimitProfile? ResolveProfile(
        HttpContext context,
        MeansRateLimitOptions limits,
        S3AddressingOptions addressing)
    {
        if (context.Request.Path.Equals("/api/console/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return new RateLimitProfile("console-login", limits.ConsoleLoginPermitLimit, limits.ConsoleLoginWindowSeconds);
        }

        if (context.Request.Path.StartsWithSegments("/api/console"))
        {
            return new RateLimitProfile("console-api", limits.ConsoleApiPermitLimit, limits.ConsoleApiWindowSeconds);
        }

        if (IsS3Request(context, addressing))
        {
            return new RateLimitProfile("s3", limits.S3PermitLimit, limits.S3WindowSeconds);
        }

        return null;
    }

    private static async Task WriteRejectedAsync(
        HttpContext context,
        S3AddressingOptions addressing,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        var retryAfterHeader = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.RetryAfter = retryAfterHeader;

        if (IsS3Request(context, addressing))
        {
            await S3ResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                MeansErrorCodes.SlowDown,
                "Rate limit exceeded. Please reduce your request rate.",
                S3RequestIds.New(),
                new Dictionary<string, string> { ["Retry-After"] = retryAfterHeader },
                cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json; charset=utf-8";
        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ConsoleApiError(
                MeansErrorCodes.SlowDown,
                "Rate limit exceeded. Please reduce your request rate.",
                StatusCodes.Status429TooManyRequests),
            MeansJsonContext.Default.ConsoleApiError,
            cancellationToken);
    }

    private static string ClientPartitionKey(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static bool IsS3Request(HttpContext context, S3AddressingOptions addressing)
    {
        if (context.Request.Path.StartsWithSegments(addressing.AliasPrefix))
        {
            return true;
        }

        var host = context.Request.Host.Host;
        return string.Equals(host, addressing.ServiceHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + addressing.DomainSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct RateLimitProfile(string Name, int PermitLimit, int WindowSeconds);
}
