namespace Means.Core;

/// <summary>
/// Operator-managed runtime settings exposed by the built-in console.
/// </summary>
public sealed record SystemSettings(long MaxUploadSizeBytes, string? PublicOrigin = null)
{
    public const long MinimumMaxUploadSizeBytes = 1L * 1024 * 1024;
    public const long DefaultMaxUploadSizeBytes = 1L * 1024 * 1024 * 1024;
    public const long MaximumMaxUploadSizeBytes = 5L * 1024 * 1024 * 1024 * 1024;

    public static SystemSettings Default { get; } = new(DefaultMaxUploadSizeBytes);

    public static long ValidateMaxUploadSizeBytes(long value)
    {
        if (value < MinimumMaxUploadSizeBytes || value > MaximumMaxUploadSizeBytes)
        {
            throw new MeansException(
                MeansErrorCodes.InvalidArgument,
                $"Max upload size must be between {MinimumMaxUploadSizeBytes} and {MaximumMaxUploadSizeBytes} bytes.",
                400);
        }

        return value;
    }

    public static string? NormalizePublicOrigin(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var candidate = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : "https://" + trimmed;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            throw new MeansException(
                MeansErrorCodes.InvalidArgument,
                "Public origin must be an HTTP or HTTPS origin, for example https://means.asia.",
                400);
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
