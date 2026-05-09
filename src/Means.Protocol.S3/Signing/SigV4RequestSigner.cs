using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Means.Protocol.S3;

/// <summary>
/// Small SigV4 signer used by tests and SDK contract fixtures.
/// The production server verifies incoming signatures; SDK packages own their language-specific
/// signer implementations but should produce the same canonical output.
/// </summary>
public static class SigV4RequestSigner
{
    public static void Sign(HttpRequestMessage request, SigV4SigningCredentials credentials, string region = "us-east-1", string service = "s3", DateTimeOffset? now = null)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("RequestUri is required.");
        }

        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = timestamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "UNSIGNED-PAYLOAD");

        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var headers = BuildHeaderLookup(request);
        var query = QueryCollectionFromUri(request.RequestUri);
        var canonicalRequest = SigV4CanonicalRequest.Build(
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            query,
            headers,
            signedHeaders,
            "UNSIGNED-PAYLOAD",
            includeSignature: true);

        var scope = new SigV4CredentialScope(credentials.AccessKey, date, region, service);
        var signature = SigV4Cryptography.ComputeSignature(credentials.SecretKey, scope, amzDate, canonicalRequest);
        var authorization = $"AWS4-HMAC-SHA256 Credential={credentials.AccessKey}/{date}/{region}/{service}/aws4_request, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    public static Uri Presign(Uri uri, HttpMethod method, SigV4SigningCredentials credentials, TimeSpan expires, string region = "us-east-1", string service = "s3", DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = timestamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var builder = new UriBuilder(uri);
        var query = ParseQuery(builder.Query);
        query["X-Amz-Algorithm"] = new StringValues("AWS4-HMAC-SHA256");
        query["X-Amz-Credential"] = new StringValues($"{credentials.AccessKey}/{date}/{region}/{service}/aws4_request");
        query["X-Amz-Date"] = new StringValues(amzDate);
        query["X-Amz-Expires"] = new StringValues(((int)expires.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        query["X-Amz-SignedHeaders"] = new StringValues("host");

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.Authority.ToLowerInvariant()
        };
        var scope = new SigV4CredentialScope(credentials.AccessKey, date, region, service);
        var canonicalRequest = SigV4CanonicalRequest.Build(
            method.Method,
            uri.AbsolutePath,
            new QueryCollection(query),
            headers,
            "host",
            "UNSIGNED-PAYLOAD",
            includeSignature: false);
        var signature = SigV4Cryptography.ComputeSignature(credentials.SecretKey, scope, amzDate, canonicalRequest);
        query["X-Amz-Signature"] = new StringValues(signature);
        builder.Query = BuildQuery(query);
        return builder.Uri;
    }

    private static Dictionary<string, string> BuildHeaderLookup(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.RequestUri!.Authority.ToLowerInvariant()
        };

        foreach (var header in request.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }

        return headers;
    }

    private static QueryCollection QueryCollectionFromUri(Uri uri)
    {
        return new QueryCollection(ParseQuery(uri.Query));
    }

    private static Dictionary<string, StringValues> ParseQuery(string query)
    {
        var result = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrEmpty(trimmed))
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            result[key] = result.TryGetValue(key, out var existing)
                ? StringValues.Concat(existing, value)
                : new StringValues(value);
        }

        return result;
    }

    private static string BuildQuery(Dictionary<string, StringValues> query)
    {
        return string.Join("&",
            query.SelectMany(pair => pair.Value.Select(value => (Key: SigV4CanonicalRequest.Escape(pair.Key), Value: SigV4CanonicalRequest.Escape(value ?? ""))))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }
}
