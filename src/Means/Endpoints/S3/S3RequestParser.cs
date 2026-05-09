using System.Globalization;
using System.Xml.Linq;
using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Small parsing helpers for S3-specific HTTP values.
/// Keeping these helpers in one place keeps endpoint handlers focused on operation flow.
/// </summary>
internal static class S3RequestParser
{
    public static IReadOnlyDictionary<string, string> ExtractMetadata(HttpContext context)
    {
        return context.Request.Headers
            .Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["x-amz-meta-".Length..].ToLowerInvariant(),
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static (string Bucket, string Key) ParseCopySource(string value)
    {
        var source = Uri.UnescapeDataString(value.Trim().TrimStart('/'));
        var slash = source.IndexOf('/');
        if (slash <= 0 || slash == source.Length - 1)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-copy-source header.", 400);
        }

        return (source[..slash], source[(slash + 1)..]);
    }

    public static (long Start, long End)? ParseRange(string value, long length)
    {
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = value["bytes=".Length..].Split('-', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return null;
            }

            return (Math.Max(0, length - suffix), length - 1);
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        var end = parts[1].Length == 0
            ? length - 1
            : long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEnd)
                ? parsedEnd
                : -1;
        return start < 0 || end < start || start >= length ? null : (start, Math.Min(end, length - 1));
    }

    public static string? GetHeader(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int ParseMaxKeys(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1000;
    }

    public static int ParseMaxUploads(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1000;
    }

    public static int ParsePartNumber(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
            || partNumber is < 1 or > 10000)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Part number must be between 1 and 10000.", 400);
        }

        return partNumber;
    }

    public static string ParseUploadId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Missing uploadId.", 400);
        }

        return value;
    }

    public static async Task<IReadOnlyList<CompletedMultipartPart>> ParseCompleteMultipartUploadAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            var document = await XDocument.LoadAsync(body, LoadOptions.None, cancellationToken);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "CompleteMultipartUpload")
            {
                throw MalformedCompleteMultipartUpload();
            }

            var parts = new List<CompletedMultipartPart>();
            foreach (var part in root.Elements().Where(element => element.Name.LocalName == "Part"))
            {
                var partNumberText = ElementValue(part, "PartNumber");
                var etag = ElementValue(part, "ETag");
                if (!int.TryParse(partNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
                    || string.IsNullOrWhiteSpace(etag))
                {
                    throw MalformedCompleteMultipartUpload();
                }

                parts.Add(new CompletedMultipartPart(partNumber, etag));
            }

            return parts;
        }
        catch (MeansException)
        {
            throw;
        }
        catch
        {
            throw MalformedCompleteMultipartUpload();
        }
    }

    public static void ValidateBucketName(string bucketName) => S3NameValidator.ValidateBucketName(bucketName);

    public static void ValidateObjectKey(string objectKey) => S3NameValidator.ValidateObjectKey(objectKey);

    private static string? ElementValue(XElement root, string name)
    {
        return root.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    }

    private static MeansException MalformedCompleteMultipartUpload()
    {
        return new MeansException(MeansErrorCodes.MalformedXML, "Malformed CompleteMultipartUpload XML.", 400);
    }
}
