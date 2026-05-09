namespace Means.Protocol.S3;

internal static class SigV4AuthorizationHeader
{
    private const string Scheme = "AWS4-HMAC-SHA256 ";

    public static bool TryParse(string value, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        fields = value[Scheme.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        return true;
    }
}
