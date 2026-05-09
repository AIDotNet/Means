using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Means.Internal;

internal static class SigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    internal static void Sign(HttpRequestMessage request, MeansCredentials credentials, string region, string service, DateTimeOffset? now = null)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("RequestUri is required before signing.");
        }

        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = timestamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", UnsignedPayload);

        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        }

        var headers = BuildHeaderLookup(request);
        var signedHeaders = string.Join(";", headers.Keys.OrderBy(value => value, StringComparer.Ordinal));
        var canonicalRequest = BuildCanonicalRequest(
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            ParseQuery(request.RequestUri.Query),
            headers,
            signedHeaders,
            UnsignedPayload,
            includeSignature: true);

        var credentialScope = $"{shortDate}/{region}/{service}/aws4_request";
        var stringToSign = BuildStringToSign(amzDate, credentialScope, canonicalRequest);
        var signature = Hex(Hmac(DeriveSigningKey(credentials.SecretKey, shortDate, region, service), stringToSign));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(
            $"{Algorithm} Credential={credentials.AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    internal static PresignedRequest Presign(
        Uri uri,
        HttpMethod method,
        MeansCredentials credentials,
        TimeSpan expires,
        string region,
        string service,
        DateTimeOffset? now = null)
    {
        if (expires <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expires), "Expiration must be greater than zero.");
        }

        if (expires > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(expires), "SigV4 presigned URLs cannot exceed seven days.");
        }

        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = timestamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var credentialScope = $"{shortDate}/{region}/{service}/aws4_request";
        var query = ParseQuery(uri.Query);

        SetQuery(query, "X-Amz-Algorithm", Algorithm);
        SetQuery(query, "X-Amz-Credential", $"{credentials.AccessKey}/{credentialScope}");
        SetQuery(query, "X-Amz-Date", amzDate);
        SetQuery(query, "X-Amz-Expires", ((int)expires.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        SetQuery(query, "X-Amz-SignedHeaders", "host");

        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            SetQuery(query, "X-Amz-Security-Token", credentials.SessionToken);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.Authority.ToLowerInvariant()
        };

        var canonicalRequest = BuildCanonicalRequest(
            method.Method,
            uri.AbsolutePath,
            query,
            headers,
            "host",
            UnsignedPayload,
            includeSignature: false);

        var stringToSign = BuildStringToSign(amzDate, credentialScope, canonicalRequest);
        var signature = Hex(Hmac(DeriveSigningKey(credentials.SecretKey, shortDate, region, service), stringToSign));
        SetQuery(query, "X-Amz-Signature", signature);

        var builder = new UriBuilder(uri)
        {
            Query = BuildQuery(query)
        };

        return new PresignedRequest(builder.Uri, method, DateTimeOffset.UtcNow.Add(expires));
    }

    private static string BuildCanonicalRequest(
        string method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, string> headers,
        string signedHeaders,
        string payloadHash,
        bool includeSignature)
    {
        var signedHeaderNames = signedHeaders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().ToLowerInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var canonicalHeaders = new StringBuilder();
        foreach (var header in signedHeaderNames)
        {
            string? value;
            headers.TryGetValue(header, out value);
            canonicalHeaders.Append(header).Append(':').Append(NormalizeHeaderValue(value ?? "")).Append('\n');
        }

        return string.Join("\n", new[]
        {
            method.ToUpperInvariant(),
            CanonicalUri(path),
            CanonicalQueryString(query, includeSignature),
            canonicalHeaders.ToString(),
            string.Join(";", signedHeaderNames),
            payloadHash
        });
    }

    private static string BuildStringToSign(string amzDate, string credentialScope, string canonicalRequest)
    {
        return string.Join("\n", new[]
        {
            Algorithm,
            amzDate,
            credentialScope,
            Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)))
        });
    }

    private static SortedDictionary<string, string> BuildHeaderLookup(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.RequestUri!.Authority.ToLowerInvariant()
        };

        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lower = header.Key.ToLowerInvariant();
            if (ShouldSignHeader(lower))
            {
                headers[lower] = string.Join(",", header.Value.Select(value => value.Trim()));
            }
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                var lower = header.Key.ToLowerInvariant();
                if (ShouldSignHeader(lower))
                {
                    headers[lower] = string.Join(",", header.Value.Select(value => value.Trim()));
                }
            }
        }

        return headers;
    }

    private static bool ShouldSignHeader(string lowerHeaderName)
    {
        return lowerHeaderName == "content-type"
            || lowerHeaderName == "cache-control"
            || lowerHeaderName == "content-disposition"
            || lowerHeaderName.StartsWith("x-amz-", StringComparison.Ordinal);
    }

    private static string CanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return string.Join("/", path.Split('/').Select(Escape));
    }

    private static string CanonicalQueryString(IEnumerable<KeyValuePair<string, string>> query, bool includeSignature)
    {
        return string.Join("&",
            query.Where(pair => includeSignature || !pair.Key.Equals("X-Amz-Signature", StringComparison.Ordinal))
                .Select(pair => new KeyValuePair<string, string>(Escape(pair.Key), Escape(pair.Value)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return result;
        }

        foreach (var pair in trimmed.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(new[] { '=' }, 2);
            result.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal)),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal)) : ""));
        }

        return result;
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> query)
    {
        return string.Join("&",
            query.Select(pair => new KeyValuePair<string, string>(Escape(pair.Key), Escape(pair.Value)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static void SetQuery(List<KeyValuePair<string, string>> query, string key, string value)
    {
        for (var i = query.Count - 1; i >= 0; i--)
        {
            if (query[i].Key.Equals(key, StringComparison.Ordinal))
            {
                query.RemoveAt(i);
            }
        }

        query.Add(new KeyValuePair<string, string>(key, value));
    }

    private static string NormalizeHeaderValue(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static byte[] DeriveSigningKey(string secretKey, string date, string region, string service)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var regionKey = Hmac(dateKey, region);
        var serviceKey = Hmac(regionKey, service);
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static byte[] Sha256(byte[] value)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(value);
    }

    private static string Hex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            chars[i * 2] = GetHexNibble(value >> 4);
            chars[i * 2 + 1] = GetHexNibble(value & 0x0F);
        }

        return new string(chars);
    }

    private static char GetHexNibble(int value)
    {
        return (char)(value < 10 ? '0' + value : 'a' + value - 10);
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
    }
}
