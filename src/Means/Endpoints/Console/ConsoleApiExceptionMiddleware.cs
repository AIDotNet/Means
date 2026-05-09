using System.Text.Json;
using Means.Core;

namespace Means.Endpoints.Console;

/// <summary>
/// Converts console API exceptions into JSON while leaving S3 XML errors under S3Endpoint control.
/// This keeps browser clients from having to parse S3 XML for management-screen failures.
/// </summary>
public static class ConsoleApiExceptionMiddleware
{
    public static IApplicationBuilder UseConsoleApiErrors(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (MeansException ex) when (context.Request.Path.StartsWithSegments("/api/console"))
            {
                await WriteJsonErrorAsync(context, ex.StatusCode, ex.Code, ex.Message);
            }
            catch (JsonException ex) when (context.Request.Path.StartsWithSegments("/api/console"))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status400BadRequest, MeansErrorCodes.InvalidArgument, ex.Message);
            }
        });
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ConsoleApiError(code, message, statusCode));
    }
}

public sealed record ConsoleApiError(string Code, string Message, int StatusCode);
