using System.Globalization;
using System.Text;
using Means.Core;

namespace Means.Protocol.S3;

/// <summary>
/// Validates the S3-compatible naming subset supported by Means v1.
/// Keeping these rules in the protocol project makes server handlers, SDK tests, and future
/// contract tests use the same bucket/key assumptions.
/// </summary>
public static class S3NameValidator
{
    public static void ValidateBucketName(string bucketName)
    {
        if (bucketName.Length is < 3 or > 63
            || !char.IsLetterOrDigit(bucketName[0])
            || !char.IsLetterOrDigit(bucketName[^1])
            || bucketName.Contains("..", StringComparison.Ordinal)
            || bucketName.Contains(".-", StringComparison.Ordinal)
            || bucketName.Contains("-.", StringComparison.Ordinal)
            || bucketName.Split('.').All(part => byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            || bucketName.Any(ch => !((ch >= 'a' && ch <= 'z') || char.IsDigit(ch) || ch is '-' or '.')))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid bucket name.", 400);
        }
    }

    public static void ValidateObjectKey(string objectKey)
    {
        // S3 object keys are byte-oriented. Means follows the common 1,024-byte ceiling so UTF-8
        // keys are accepted while oversized encoded names are rejected before storage.
        if (string.IsNullOrEmpty(objectKey)
            || Encoding.UTF8.GetByteCount(objectKey) > 1024
            || objectKey.Any(char.IsControl)
            || objectKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid object key.", 400);
        }
    }
}
