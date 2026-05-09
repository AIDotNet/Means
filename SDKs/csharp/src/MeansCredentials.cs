namespace Means;

/// <summary>
/// Access key credentials for SigV4 request signing.
/// </summary>
public sealed class MeansCredentials
{
    /// <summary>
    /// Creates access key credentials.
    /// </summary>
    public MeansCredentials(string accessKey, string secretKey, string? sessionToken = null)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new ArgumentException("Access key is required.", nameof(accessKey));
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException("Secret key is required.", nameof(secretKey));
        }

        AccessKey = accessKey;
        SecretKey = secretKey;
        SessionToken = sessionToken;
    }

    /// <summary>
    /// Access key ID.
    /// </summary>
    public string AccessKey { get; }

    /// <summary>
    /// Secret access key.
    /// </summary>
    public string SecretKey { get; }

    /// <summary>
    /// Optional session token. Means v1 does not require this for long-lived access keys.
    /// </summary>
    public string? SessionToken { get; }
}
