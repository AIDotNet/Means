namespace Means.Protocol.S3;

/// <summary>
/// Implements static-object content negotiation for the S3 data plane.
/// Compression is resolved inside the object response writer instead of using a global
/// middleware so Range requests can always receive the original byte representation.
/// </summary>
public static class S3Compression
{
    private static readonly HashSet<string> CompressibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/javascript",
        "application/json",
        "application/xml",
        "image/svg+xml",
        "text/css",
        "text/html",
        "text/javascript",
        "text/plain",
        "text/xml"
    };

    public static string? Negotiate(string? acceptEncoding, string contentType, long contentLength, bool hasRangeHeader)
    {
        // Encoded byte ranges are difficult to reason about and easy for clients to cache incorrectly.
        // v1 therefore disables compression whenever Range is present.
        if (hasRangeHeader || contentLength < 1024 || string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return null;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        if (!CompressibleTypes.Contains(mediaType))
        {
            return null;
        }

        var encodings = acceptEncoding.Split(',').Select(value => value.Trim().ToLowerInvariant()).ToArray();
        if (encodings.Any(value => value == "br" || value.StartsWith("br;", StringComparison.Ordinal)))
        {
            return "br";
        }

        if (encodings.Any(value => value == "gzip" || value.StartsWith("gzip;", StringComparison.Ordinal)))
        {
            return "gzip";
        }

        return null;
    }
}
