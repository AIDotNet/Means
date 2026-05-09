using System.Globalization;
using Means.Core;
using Microsoft.AspNetCore.Http;

namespace Means.Protocol.S3;

/// <summary>
/// Verifies S3 SigV4 authentication for both Authorization headers and query presigned URLs.
/// The verifier only authenticates the principal; bucket policy evaluation remains a separate
/// authorization step in the endpoint layer.
/// </summary>
public sealed class SigV4RequestVerifier
{
    private const int MaxPresignExpiresSeconds = 7 * 24 * 60 * 60;

    public async Task<SigV4AuthResult> VerifyAsync(
        HttpRequest request,
        Func<string, CancellationToken, Task<AccessKeyCredential?>> credentialResolver,
        CancellationToken cancellationToken)
    {
        if (request.Query.ContainsKey("X-Amz-Algorithm"))
        {
            return await VerifyPresignedAsync(request, credentialResolver, cancellationToken);
        }

        if (request.Headers.Authorization.Count > 0)
        {
            return await VerifyHeaderAsync(request, credentialResolver, cancellationToken);
        }

        return SigV4AuthResult.Anonymous;
    }

    private static async Task<SigV4AuthResult> VerifyHeaderAsync(
        HttpRequest request,
        Func<string, CancellationToken, Task<AccessKeyCredential?>> credentialResolver,
        CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!SigV4AuthorizationHeader.TryParse(authorization, out var fields))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Unsupported authorization scheme.");
        }

        if (!fields.TryGetValue("Credential", out var credentialValue)
            || !fields.TryGetValue("SignedHeaders", out var signedHeaders)
            || !fields.TryGetValue("Signature", out var providedSignature))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Malformed authorization header.");
        }

        var credential = SigV4CredentialScope.Parse(credentialValue);
        if (credential is null)
        {
            return Failure(MeansErrorCodes.AccessDenied, "Malformed credential scope.");
        }

        var storedCredential = await credentialResolver(credential.AccessKey, cancellationToken);
        if (storedCredential is not { Enabled: true })
        {
            return Failure(MeansErrorCodes.AccessDenied, "Unknown or disabled access key.");
        }

        var amzDate = request.Headers["x-amz-date"].ToString();
        if (string.IsNullOrWhiteSpace(amzDate))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Missing x-amz-date.");
        }

        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault() ?? "UNSIGNED-PAYLOAD";
        var canonicalRequest = SigV4CanonicalRequest.Build(
            request.Method,
            request.Path.Value ?? "/",
            request.Query,
            SigV4CanonicalRequest.BuildHeaderLookup(request),
            signedHeaders,
            payloadHash,
            includeSignature: true);
        var expectedSignature = SigV4Cryptography.ComputeSignature(storedCredential.SecretKey, credential, amzDate, canonicalRequest);

        return SigV4Cryptography.FixedTimeEquals(expectedSignature, providedSignature)
            ? new SigV4AuthResult(true, true, credential.AccessKey, null, null)
            : Failure(MeansErrorCodes.SignatureDoesNotMatch, "The request signature does not match.");
    }

    private static async Task<SigV4AuthResult> VerifyPresignedAsync(
        HttpRequest request,
        Func<string, CancellationToken, Task<AccessKeyCredential?>> credentialResolver,
        CancellationToken cancellationToken)
    {
        var algorithm = request.Query["X-Amz-Algorithm"].ToString();
        if (!algorithm.Equals("AWS4-HMAC-SHA256", StringComparison.Ordinal))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Unsupported presign algorithm.");
        }

        var credential = SigV4CredentialScope.Parse(request.Query["X-Amz-Credential"].ToString());
        if (credential is null)
        {
            return Failure(MeansErrorCodes.AccessDenied, "Malformed credential scope.");
        }

        var signature = request.Query["X-Amz-Signature"].ToString();
        var signedHeaders = request.Query["X-Amz-SignedHeaders"].ToString();
        var amzDate = request.Query["X-Amz-Date"].ToString();
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(signedHeaders) || string.IsNullOrWhiteSpace(amzDate))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Missing presign parameters.");
        }

        if (!DateTimeOffset.TryParseExact(
                amzDate,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var signedAt))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Invalid presign timestamp.");
        }

        if (!int.TryParse(request.Query["X-Amz-Expires"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var expiresSeconds)
            || expiresSeconds <= 0)
        {
            return Failure(MeansErrorCodes.AccessDenied, "Invalid presign expiration.");
        }

        if (expiresSeconds > MaxPresignExpiresSeconds)
        {
            return Failure(MeansErrorCodes.AccessDenied, "Presign expiration exceeds the maximum allowed duration.");
        }

        if (DateTimeOffset.UtcNow > signedAt.AddSeconds(expiresSeconds))
        {
            return Failure(MeansErrorCodes.AccessDenied, "Presigned URL has expired.");
        }

        var storedCredential = await credentialResolver(credential.AccessKey, cancellationToken);
        if (storedCredential is not { Enabled: true })
        {
            return Failure(MeansErrorCodes.AccessDenied, "Unknown or disabled access key.");
        }

        var canonicalRequest = SigV4CanonicalRequest.Build(
            request.Method,
            request.Path.Value ?? "/",
            request.Query,
            SigV4CanonicalRequest.BuildHeaderLookup(request),
            signedHeaders,
            "UNSIGNED-PAYLOAD",
            includeSignature: false);
        var expectedSignature = SigV4Cryptography.ComputeSignature(storedCredential.SecretKey, credential, amzDate, canonicalRequest);

        return SigV4Cryptography.FixedTimeEquals(expectedSignature, signature)
            ? new SigV4AuthResult(true, true, credential.AccessKey, null, null)
            : Failure(MeansErrorCodes.SignatureDoesNotMatch, "The request signature does not match.");
    }

    private static SigV4AuthResult Failure(string code, string message)
    {
        return new SigV4AuthResult(true, false, null, code, message);
    }
}
