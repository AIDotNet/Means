using System.Globalization;
using System.IO.Compression;
using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Centralizes S3 response rendering: XML envelopes, error payloads, object headers, Range streaming, and compression.
/// Endpoint handlers call this type instead of directly shaping wire-level responses.
/// </summary>
internal static class S3ResponseWriter
{
    public static void StartTraceHeaders(HttpContext context, string requestId)
    {
        context.Response.Headers["x-amz-request-id"] = requestId;
        context.Response.Headers["x-amz-id-2"] = S3RequestIds.New() + S3RequestIds.New();
    }

    public static async Task WriteXmlAsync(HttpContext context, int statusCode, string xml, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(xml, cancellationToken);
    }

    public static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        string requestId,
        IReadOnlyDictionary<string, string>? responseHeaders,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml";
        context.Response.Headers["x-amz-request-id"] = requestId;
        context.Response.Headers["x-amz-id-2"] = S3RequestIds.New() + S3RequestIds.New();
        foreach (var header in responseHeaders ?? new Dictionary<string, string>())
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(S3Xml.Error(code, message, context.Request.Path, requestId), cancellationToken);
        }
    }

    public static async Task WriteObjectAsync(
        HttpContext context,
        IObjectStore store,
        string bucketName,
        string key,
        BucketSettings bucketSettings,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        await using var data = await store.GetObjectAsync(bucketName, key, cancellationToken);
        var info = data.Info;
        WriteObjectHeaders(context, info, bucketSettings);

        var rangeHeader = context.Request.Headers.Range.ToString();
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            await WriteRangeAsync(context, data.Content, info.ContentLength, rangeHeader, headOnly, cancellationToken);
            return;
        }

        var encoding = S3Compression.Negotiate(context.Request.Headers.AcceptEncoding.ToString(), info.ContentType, info.ContentLength, hasRangeHeader: false);
        if (encoding is not null)
        {
            await WriteCompressedAsync(context, data.Content, info.ETag, encoding, headOnly, cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = info.ContentLength;
        if (!headOnly)
        {
            context.Items[S3MetricsItems.EgressBytes] = info.ContentLength;
            await data.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private static async Task WriteCompressedAsync(HttpContext context, Stream input, string sourceEtag, string encoding, bool headOnly, CancellationToken cancellationToken)
    {
        await using var compressed = new MemoryStream();
        await CompressAsync(input, compressed, encoding, cancellationToken);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentEncoding = encoding;
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.Headers.ETag = $"W/\"{sourceEtag}-{encoding}\"";
        context.Response.ContentLength = compressed.Length;
        if (!headOnly)
        {
            context.Items[S3MetricsItems.EgressBytes] = compressed.Length;
            compressed.Position = 0;
            await compressed.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private static async Task WriteRangeAsync(HttpContext context, Stream content, long contentLength, string rangeHeader, bool headOnly, CancellationToken cancellationToken)
    {
        var range = S3RequestParser.ParseRange(rangeHeader, contentLength);
        if (range is null)
        {
            throw new MeansException(
                MeansErrorCodes.InvalidRange,
                "Invalid range.",
                416,
                new Dictionary<string, string> { ["Content-Range"] = $"bytes */{contentLength}" });
        }

        var (start, end) = range.Value;
        var length = end - start + 1;
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers.ContentRange = $"bytes {start}-{end}/{contentLength}";
        context.Response.ContentLength = length;
        if (headOnly)
        {
            return;
        }

        context.Items[S3MetricsItems.EgressBytes] = length;
        content.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1024 * 64];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static void WriteObjectHeaders(HttpContext context, ObjectInfo info, BucketSettings bucketSettings)
    {
        context.Response.ContentType = info.ContentType;
        context.Response.Headers.ETag = S3Xml.QuoteEtag(info.ETag);
        context.Response.Headers.LastModified = info.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers.AcceptRanges = "bytes";
        if (!string.IsNullOrWhiteSpace(info.CacheControl))
        {
            context.Response.Headers.CacheControl = info.CacheControl;
        }
        else if (TryGetDefaultHeader(bucketSettings, "Cache-Control", out var defaultCacheControl))
        {
            context.Response.Headers.CacheControl = defaultCacheControl;
        }

        if (!string.IsNullOrWhiteSpace(info.ContentDisposition))
        {
            context.Response.Headers.ContentDisposition = info.ContentDisposition;
        }
        else if (TryGetDefaultHeader(bucketSettings, "Content-Disposition", out var defaultContentDisposition))
        {
            context.Response.Headers.ContentDisposition = defaultContentDisposition;
        }

        if (TryGetDefaultHeader(bucketSettings, "Content-Language", out var contentLanguage))
        {
            context.Response.Headers["Content-Language"] = contentLanguage;
        }

        if (TryGetDefaultHeader(bucketSettings, "Expires", out var expires))
        {
            context.Response.Headers["Expires"] = expires;
        }

        var metadata = new Dictionary<string, string>(bucketSettings.DefaultMetadata, StringComparer.OrdinalIgnoreCase);
        foreach (var item in info.Metadata)
        {
            metadata[item.Key] = item.Value;
        }

        foreach (var item in metadata)
        {
            context.Response.Headers["x-amz-meta-" + item.Key] = item.Value;
        }
    }

    private static bool TryGetDefaultHeader(BucketSettings settings, string headerName, out string value)
    {
        if (settings.DefaultResponseHeaders.TryGetValue(headerName, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            value = configured;
            return true;
        }

        value = "";
        return false;
    }

    private static async Task CompressAsync(Stream input, Stream output, string encoding, CancellationToken cancellationToken)
    {
        Stream compressor = encoding == "br"
            ? new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using (compressor)
        {
            await input.CopyToAsync(compressor, cancellationToken);
        }
    }
}
