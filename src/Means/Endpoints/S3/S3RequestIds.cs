namespace Means.Endpoints.S3;

/// <summary>
/// Generates S3-compatible request identifiers for tracing responses and XML errors.
/// </summary>
internal static class S3RequestIds
{
    public static string New()
    {
        return Convert.ToHexString(Guid.NewGuid().ToByteArray()).Replace("-", "", StringComparison.Ordinal);
    }
}
