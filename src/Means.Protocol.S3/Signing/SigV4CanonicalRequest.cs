using System.Text;
using Microsoft.AspNetCore.Http;

namespace Means.Protocol.S3;

internal static class SigV4CanonicalRequest
{
    public static string Build(
        string method,
        string path,
        IQueryCollection query,
        IReadOnlyDictionary<string, string> headers,
        string signedHeaders,
        string payloadHash,
        bool includeSignature)
    {
        var signedHeaderNames = signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var canonicalHeaders = new StringBuilder();
        foreach (var header in signedHeaderNames)
        {
            headers.TryGetValue(header, out var value);
            canonicalHeaders.Append(header).Append(':').Append(NormalizeHeaderValue(value ?? "")).Append('\n');
        }

        return string.Join('\n',
            method.ToUpperInvariant(),
            CanonicalUri(path),
            CanonicalQueryString(query, includeSignature),
            canonicalHeaders.ToString(),
            string.Join(';', signedHeaderNames),
            payloadHash);
    }

    public static string CanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        // SigV4 canonicalization escapes every path segment but preserves path separators.
        return string.Join('/', path.Split('/').Select(Escape));
    }

    public static Dictionary<string, string> BuildHeaderLookup(HttpRequest request)
    {
        var headers = request.Headers.ToDictionary(
            header => header.Key.ToLowerInvariant(),
            header => string.Join(",", header.Value.Select(value => value?.Trim()).Where(value => !string.IsNullOrEmpty(value))),
            StringComparer.Ordinal);
        headers["host"] = (request.Host.Value ?? "").ToLowerInvariant();
        return headers;
    }

    private static string CanonicalQueryString(IQueryCollection query, bool includeSignature)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var item in query)
        {
            if (!includeSignature && item.Key.Equals("X-Amz-Signature", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var value in item.Value)
            {
                pairs.Add((Escape(item.Key), Escape(value ?? "")));
            }
        }

        return string.Join("&", pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string NormalizeHeaderValue(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Escape(string value)
    {
        return Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
    }
}
