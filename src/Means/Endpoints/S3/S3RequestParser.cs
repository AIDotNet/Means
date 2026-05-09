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

    public static (string Bucket, string Key, string? VersionId) ParseCopySource(string value)
    {
        var source = Uri.UnescapeDataString(value.Trim().TrimStart('/'));
        var queryIndex = source.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? source[..queryIndex] : source;
        var query = queryIndex >= 0 ? source[(queryIndex + 1)..] : "";
        var slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-copy-source header.", 400);
        }

        var versionId = ParseQueryValue(query, "versionId");
        return (path[..slash], path[(slash + 1)..], versionId);
    }

    public static string ParseMetadataDirective(HttpContext context)
    {
        var directive = context.Request.Headers["x-amz-metadata-directive"].ToString();
        if (string.IsNullOrWhiteSpace(directive))
        {
            return CopyMetadataDirectives.Copy;
        }

        if (string.Equals(directive, CopyMetadataDirectives.Copy, StringComparison.OrdinalIgnoreCase))
        {
            return CopyMetadataDirectives.Copy;
        }

        if (string.Equals(directive, CopyMetadataDirectives.Replace, StringComparison.OrdinalIgnoreCase))
        {
            return CopyMetadataDirectives.Replace;
        }

        throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid x-amz-metadata-directive header.", 400);
    }

    public static (long Start, long End)? ParseCopySourceRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseRange(value, length)
            ?? throw new MeansException(MeansErrorCodes.InvalidRange, "Invalid copy source range.", 400);
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

    public static int ParseMaxParts(string? value)
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

    public static async Task<string> ParseBucketVersioningStatusAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body == Stream.Null || body.CanSeek && body.Length == 0)
        {
            return BucketVersioningStatuses.Off;
        }

        var document = await LoadXmlAsync(body, cancellationToken);
        if (document.Root is null || document.Root.Name.LocalName != "VersioningConfiguration")
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Malformed VersioningConfiguration XML.", 400);
        }

        return document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "Status")?.Value
            ?? BucketVersioningStatuses.Off;
    }

    public static async Task<ObjectTagSet> ParseTaggingAsync(Stream body, CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(body, cancellationToken);
        if (document.Root is null || document.Root.Name.LocalName != "Tagging")
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Malformed Tagging XML.", 400);
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var tagSet = document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "TagSet");
        if (tagSet is not null)
        {
            foreach (var tag in tagSet.Elements().Where(element => element.Name.LocalName == "Tag"))
            {
                var key = ElementValue(tag, "Key");
                var value = ElementValue(tag, "Value") ?? "";
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new MeansException(MeansErrorCodes.InvalidTag, "Tag key is required.", 400);
                }

                tags[key] = value;
            }
        }

        return new ObjectTagSet(tags);
    }

    public static async Task<BucketLifecycleConfiguration> ParseLifecycleAsync(Stream body, CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(body, cancellationToken);
        if (document.Root is null || document.Root.Name.LocalName != "LifecycleConfiguration")
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Malformed LifecycleConfiguration XML.", 400);
        }

        var rules = new List<LifecycleRule>();
        foreach (var rule in document.Root.Elements().Where(element => element.Name.LocalName == "Rule"))
        {
            var id = ElementValue(rule, "ID") ?? Guid.NewGuid().ToString("N");
            var status = ElementValue(rule, "Status") ?? "Disabled";
            var prefix = ElementValue(rule, "Prefix")
                ?? rule.Elements().FirstOrDefault(element => element.Name.LocalName == "Filter")
                    ?.Elements().FirstOrDefault(element => element.Name.LocalName == "Prefix")?.Value
                ?? "";
            var expirationDays = ParseOptionalPositiveInt(rule.Elements().FirstOrDefault(element => element.Name.LocalName == "Expiration"), "Days");
            var noncurrentDays = ParseOptionalPositiveInt(rule.Elements().FirstOrDefault(element => element.Name.LocalName == "NoncurrentVersionExpiration"), "NoncurrentDays");
            var abortDays = ParseOptionalPositiveInt(rule.Elements().FirstOrDefault(element => element.Name.LocalName == "AbortIncompleteMultipartUpload"), "DaysAfterInitiation");
            rules.Add(new LifecycleRule(id, status, prefix, expirationDays, noncurrentDays, abortDays));
        }

        return new BucketLifecycleConfiguration(rules);
    }

    public static async Task<string> ReadAndValidateXmlAsync(Stream body, string rootName, CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(body, cancellationToken);
        if (document.Root is null || document.Root.Name.LocalName != rootName)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, $"Malformed {rootName} XML.", 400);
        }

        return document.ToString(SaveOptions.DisableFormatting);
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

    private static async Task<XDocument> LoadXmlAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            return await XDocument.LoadAsync(body, LoadOptions.None, cancellationToken);
        }
        catch (MeansException)
        {
            throw;
        }
        catch
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Malformed XML document.", 400);
        }
    }

    private static int? ParseOptionalPositiveInt(XElement? root, string name)
    {
        var value = root is null ? null : ElementValue(root, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new MeansException(MeansErrorCodes.MalformedXML, "Lifecycle numeric values must be positive integers.", 400);
    }

    private static string? ParseQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static MeansException MalformedCompleteMultipartUpload()
    {
        return new MeansException(MeansErrorCodes.MalformedXML, "Malformed CompleteMultipartUpload XML.", 400);
    }
}
