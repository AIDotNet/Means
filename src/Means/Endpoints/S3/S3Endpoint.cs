using System.Globalization;
using System.Text.Json;
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
            context.Response.Headers.AccessControlMaxAge = maxAge.Value.ToString(CultureInfo.InvariantCulture);
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
        foreach (var rule in Elements(xml, "CORSRule"))
        {
            var origins = ElementValues(rule, "AllowedOrigin").ToArray();
            var methods = ElementValues(rule, "AllowedMethod").ToArray();
            if (!origins.Any(value => value == "*" || string.Equals(value, origin, StringComparison.OrdinalIgnoreCase))
                || !methods.Any(value => string.Equals(value, method, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            allowedHeaders = string.Join(", ", ElementValues(rule, "AllowedHeader"));
            exposeHeaders = string.Join(", ", ElementValues(rule, "ExposeHeader"));
            maxAge = int.TryParse(ElementValues(rule, "MaxAgeSeconds").FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ElementValues(string xml, string localName)
    {
        foreach (var content in Elements(xml, localName))
        {
            yield return DecodeXml(content);
        }
    }

    private static IEnumerable<string> Elements(string xml, string localName)
    {
        var position = 0;
        while (TryReadNextElement(xml, position, out var name, out var content, out var next))
        {
            if (string.Equals(name, localName, StringComparison.Ordinal))
            {
                yield return content;
            }

            foreach (var child in Elements(content, localName))
            {
                yield return child;
            }

            position = next;
        }
    }

    private static bool TryReadNextElement(
        string xml,
        int start,
        out string localName,
        out string content,
        out int next)
    {
        localName = "";
        content = "";
        next = start;
        var position = start;
        while (position < xml.Length)
        {
            var openStart = xml.IndexOf('<', position);
            if (openStart < 0 || openStart + 1 >= xml.Length)
            {
                return false;
            }

            var marker = xml[openStart + 1];
            if (marker == '/')
            {
                position = openStart + 2;
                continue;
            }

            if (marker == '?')
            {
                position = SkipUntil(xml, openStart + 2, "?>");
                continue;
            }

            if (marker == '!')
            {
                position = xml.AsSpan(openStart).StartsWith("<!--", StringComparison.Ordinal)
                    ? SkipUntil(xml, openStart + 4, "-->")
                    : SkipUntil(xml, openStart + 2, ">");
                continue;
            }

            var nameStart = openStart + 1;
            var nameEnd = nameStart;
            while (nameEnd < xml.Length && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '/' && xml[nameEnd] != '>')
            {
                nameEnd++;
            }

            if (nameEnd == nameStart)
            {
                return false;
            }

            localName = LocalName(xml[nameStart..nameEnd]);
            var openEnd = xml.IndexOf('>', nameEnd);
            if (openEnd < 0)
            {
                return false;
            }

            if (IsSelfClosing(xml, openEnd))
            {
                next = openEnd + 1;
                return true;
            }

            var closeStart = FindCloseTag(xml, localName, openEnd + 1);
            if (closeStart < 0)
            {
                return false;
            }

            var closeEnd = xml.IndexOf('>', closeStart);
            if (closeEnd < 0)
            {
                return false;
            }

            content = xml[(openEnd + 1)..closeStart];
            next = closeEnd + 1;
            return true;
        }

        return false;
    }

    private static int FindCloseTag(string xml, string localName, int start)
    {
        var position = start;
        while (position < xml.Length)
        {
            var closeStart = xml.IndexOf("</", position, StringComparison.Ordinal);
            if (closeStart < 0)
            {
                return -1;
            }

            var nameStart = closeStart + 2;
            var nameEnd = nameStart;
            while (nameEnd < xml.Length && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '>')
            {
                nameEnd++;
            }

            if (string.Equals(LocalName(xml[nameStart..nameEnd]), localName, StringComparison.Ordinal))
            {
                return closeStart;
            }

            position = nameEnd;
        }

        return -1;
    }

    private static int SkipUntil(string xml, int start, string marker)
    {
        var end = xml.IndexOf(marker, start, StringComparison.Ordinal);
        return end < 0 ? xml.Length : end + marker.Length;
    }

    private static bool IsSelfClosing(string xml, int openEnd)
    {
        var index = openEnd - 1;
        while (index >= 0 && char.IsWhiteSpace(xml[index]))
        {
            index--;
        }

        return index >= 0 && xml[index] == '/';
    }

    private static string LocalName(string name)
    {
        var separator = name.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private static string DecodeXml(string value)
    {
        return value
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
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
