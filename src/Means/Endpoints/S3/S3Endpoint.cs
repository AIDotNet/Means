using System.Text.Json;
using System.Xml.Linq;
using Means.Core;
using Means.Protocol.S3;
using Microsoft.Extensions.Options;

namespace Means.Endpoints.S3;

/// <summary>
/// Single ASP.NET Core entry point for the S3-compatible data plane.
/// This class intentionally stays thin: it creates request tracing headers, resolves bucket addressing,
/// and delegates business work to smaller endpoint components.
/// </summary>
public static class S3Endpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        IObjectStore store,
        IBucketPolicyRepository policies,
        IAccessKeyStore accessKeys,
        IConsoleStore consoleStore,
        BucketPolicyEvaluator policyEvaluator,
        SigV4RequestVerifier verifier,
        IOptions<S3AddressingOptions> addressingOptions,
        CancellationToken cancellationToken)
    {
        var requestId = S3RequestIds.New();
        S3Address? address = null;
        var isListOperation = false;
        var handledError = false;
        S3ResponseWriter.StartTraceHeaders(context, requestId);

        try
        {
            address = S3AddressResolver.Resolve(context.Request, addressingOptions.Value);
            isListOperation = IsListOperation(context, address);
            var authorizer = new S3RequestAuthorizer(accessKeys, policies, policyEvaluator, verifier);

            if (HttpMethods.IsOptions(context.Request.Method) && address.BucketName is not null)
            {
                await HandleCorsPreflightAsync(context, store, address.BucketName, cancellationToken);
                return;
            }

            if (address.BucketName is not null && context.Request.Query.ContainsKey("policy"))
            {
                await S3PolicyEndpoint.HandleAsync(context, address.BucketName, policies, authorizer, requestId, cancellationToken);
                return;
            }

            await S3RequestRouter.DispatchAsync(context, address, store, consoleStore, authorizer, cancellationToken);
        }
        catch (MeansException ex)
        {
            handledError = true;
            await S3ResponseWriter.WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message, requestId, ex.ResponseHeaders, cancellationToken);
        }
        catch (JsonException)
        {
            handledError = true;
            await S3ResponseWriter.WriteErrorAsync(context, StatusCodes.Status400BadRequest, MeansErrorCodes.InvalidArgument, "Invalid JSON document.", requestId, responseHeaders: null, cancellationToken);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            handledError = true;
            await S3ResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                MeansErrorCodes.EntityTooLarge,
                "Request body exceeds the configured upload limit.",
                requestId,
                responseHeaders: null,
                cancellationToken);
        }
        catch
        {
            handledError = true;
            throw;
        }
        finally
        {
            await TryRecordMetricAsync(context, consoleStore, address, isListOperation, handledError);
        }
    }

    private static bool IsListOperation(HttpContext context, S3Address address)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        if (address.BucketName is null)
        {
            return true;
        }

        if (address.ObjectKey is null)
        {
            return context.Request.Query["list-type"] == "2"
                || context.Request.Query.ContainsKey("uploads");
        }

        return context.Request.Query.ContainsKey("uploadId");
    }

    private static async Task HandleCorsPreflightAsync(
        HttpContext context,
        IObjectStore store,
        string bucketName,
        CancellationToken cancellationToken)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(requestedMethod))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var configuration = await store.GetBucketCorsAsync(bucketName, cancellationToken);
        if (configuration is null || !TryMatchCorsRule(configuration.Xml, origin, requestedMethod, out var allowedHeaders, out var exposeHeaders, out var maxAge))
        {
            throw new MeansException(MeansErrorCodes.AccessDenied, "CORS is not enabled for this origin or method.", 403);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.AccessControlAllowMethods = requestedMethod;
        if (!string.IsNullOrWhiteSpace(allowedHeaders))
        {
            context.Response.Headers.AccessControlAllowHeaders = allowedHeaders;
        }

        if (!string.IsNullOrWhiteSpace(exposeHeaders))
        {
            context.Response.Headers.AccessControlExposeHeaders = exposeHeaders;
        }

        if (maxAge is not null)
        {
            context.Response.Headers.AccessControlMaxAge = maxAge.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static bool TryMatchCorsRule(
        string xml,
        string origin,
        string method,
        out string allowedHeaders,
        out string exposeHeaders,
        out int? maxAge)
    {
        allowedHeaders = "";
        exposeHeaders = "";
        maxAge = null;
        var document = XDocument.Parse(xml);
        foreach (var rule in document.Root?.Elements().Where(element => element.Name.LocalName == "CORSRule") ?? [])
        {
            var origins = rule.Elements().Where(element => element.Name.LocalName == "AllowedOrigin").Select(element => element.Value).ToArray();
            var methods = rule.Elements().Where(element => element.Name.LocalName == "AllowedMethod").Select(element => element.Value).ToArray();
            if (!origins.Any(value => value == "*" || string.Equals(value, origin, StringComparison.OrdinalIgnoreCase))
                || !methods.Any(value => string.Equals(value, method, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            allowedHeaders = string.Join(", ", rule.Elements().Where(element => element.Name.LocalName == "AllowedHeader").Select(element => element.Value));
            exposeHeaders = string.Join(", ", rule.Elements().Where(element => element.Name.LocalName == "ExposeHeader").Select(element => element.Value));
            var maxAgeValue = rule.Elements().FirstOrDefault(element => element.Name.LocalName == "MaxAgeSeconds")?.Value;
            maxAge = int.TryParse(maxAgeValue, out var parsed) ? parsed : null;
            return true;
        }

        return false;
    }

    private static async Task TryRecordMetricAsync(
        HttpContext context,
        IConsoleStore consoleStore,
        S3Address? address,
        bool isListOperation,
        bool handledError)
    {
        try
        {
            var ingressBytes = GetMetricItem(context, S3MetricsItems.IngressBytes);
            if (ingressBytes == 0 && HttpMethods.IsPut(context.Request.Method))
            {
                ingressBytes = context.Request.ContentLength ?? 0;
            }

            await consoleStore.RecordRequestMetricAsync(
                new ConsoleRequestMetric(
                    DateTimeOffset.UtcNow,
                    address?.BucketName,
                    context.Request.Method,
                    isListOperation,
                    handledError || context.Response.StatusCode >= StatusCodes.Status400BadRequest,
                    ingressBytes,
                    GetMetricItem(context, S3MetricsItems.EgressBytes)),
                CancellationToken.None);
        }
        catch
        {
            // Metrics must never change data-plane behavior.
        }
    }

    private static long GetMetricItem(HttpContext context, string key)
    {
        if (!context.Items.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            _ => 0
        };
    }
}
