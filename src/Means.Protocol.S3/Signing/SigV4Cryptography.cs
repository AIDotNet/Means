using System.Security.Cryptography;
using System.Text;

namespace Means.Protocol.S3;

internal static class SigV4Cryptography
{
    public static string ComputeSignature(string secretKey, SigV4CredentialScope credential, string amzDate, string canonicalRequest)
    {
        var scope = $"{credential.Date}/{credential.Region}/{credential.Service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signingKey = DeriveSigningKey(secretKey, credential.Date, credential.Region, credential.Service);
        return Hex(Hmac(signingKey, stringToSign));
    }

    public static byte[] DeriveSigningKey(string secretKey, string date, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    public static string Hex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static byte[] Hmac(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }
}
