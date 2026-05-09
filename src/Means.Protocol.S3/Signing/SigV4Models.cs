namespace Means.Protocol.S3;

/// <summary>
/// Result of inspecting SigV4 header or query authentication.
/// Anonymous requests are represented explicitly so policy evaluation can still decide
/// whether public read access is allowed.
/// </summary>
public sealed record SigV4AuthResult(bool IsSigned, bool IsAuthenticated, string? AccessKey, string? ErrorCode, string? ErrorMessage)
{
    public static SigV4AuthResult Anonymous { get; } = new(false, false, null, null, null);
}

/// <summary>
/// Access key material used by server tests and SDK helpers when creating signed requests.
/// </summary>
public sealed record SigV4SigningCredentials(string AccessKey, string SecretKey);

/// <summary>
/// Parsed AWS credential scope: access key, date, region, service, and terminal aws4_request marker.
/// Means v1 accepts the standard S3 SigV4 scope shape while keeping region single-site by convention.
/// </summary>
internal sealed record SigV4CredentialScope(string AccessKey, string Date, string Region, string Service)
{
    public static SigV4CredentialScope? Parse(string value)
    {
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[4] != "aws4_request")
        {
            return null;
        }

        return new SigV4CredentialScope(parts[0], parts[1], parts[2], parts[3]);
    }
}
