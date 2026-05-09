using System.Globalization;
using System.Xml.Linq;

namespace Means.Internal;

internal static class S3XmlParser
{
    internal static IReadOnlyList<BucketSummary> ParseBuckets(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<BucketSummary>();
        }

        var document = XDocument.Parse(xml);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "Bucket")
            .Select(bucket => new BucketSummary(
                ElementValue(bucket, "Name") ?? "",
                ParseDate(ElementValue(bucket, "CreationDate"))))
            .Where(bucket => bucket.Name.Length > 0)
            .ToArray();
    }

    internal static ListObjectsResult ParseObjects(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("ListObjects response did not contain a root element.");

        var result = new ListObjectsResult
        {
            BucketName = ElementValue(root, "Name") ?? "",
            Prefix = EmptyToNull(ElementValue(root, "Prefix")),
            Delimiter = EmptyToNull(ElementValue(root, "Delimiter")),
            KeyCount = ParseInt(ElementValue(root, "KeyCount")) ?? 0,
            MaxKeys = ParseInt(ElementValue(root, "MaxKeys")),
            IsTruncated = string.Equals(ElementValue(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase),
            NextContinuationToken = EmptyToNull(ElementValue(root, "NextContinuationToken"))
        };

        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "Contents"))
        {
            result.Objects.Add(new ObjectSummary
            {
                Key = ElementValue(element, "Key") ?? "",
                ETag = NormalizeETag(ElementValue(element, "ETag")),
                Size = ParseLong(ElementValue(element, "Size")) ?? 0,
                LastModified = ParseDate(ElementValue(element, "LastModified")),
                StorageClass = EmptyToNull(ElementValue(element, "StorageClass")),
                ContentType = EmptyToNull(ElementValue(element, "ContentType"))
            });
        }

        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "CommonPrefixes"))
        {
            var prefix = ElementValue(element, "Prefix");
            if (!string.IsNullOrEmpty(prefix))
            {
                result.CommonPrefixes.Add(prefix);
            }
        }

        return result;
    }

    internal static CopyObjectResult ParseCopyObject(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("CopyObject response did not contain a root element.");

        return new CopyObjectResult
        {
            ETag = NormalizeETag(ElementValue(root, "ETag")),
            LastModified = ParseDate(ElementValue(root, "LastModified"))
        };
    }

    internal static MultipartUploadResult ParseInitiateMultipartUpload(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("InitiateMultipartUpload response did not contain a root element.");

        return new MultipartUploadResult
        {
            BucketName = ElementValue(root, "Bucket") ?? "",
            Key = ElementValue(root, "Key") ?? "",
            UploadId = ElementValue(root, "UploadId") ?? ""
        };
    }

    internal static CompleteMultipartUploadResult ParseCompleteMultipartUpload(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("CompleteMultipartUpload response did not contain a root element.");

        return new CompleteMultipartUploadResult
        {
            BucketName = ElementValue(root, "Bucket") ?? "",
            Key = ElementValue(root, "Key") ?? "",
            Location = EmptyToNull(ElementValue(root, "Location")),
            ETag = NormalizeETag(ElementValue(root, "ETag"))
        };
    }

    internal static ListPartsResult ParseParts(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("ListParts response did not contain a root element.");
        var result = new ListPartsResult
        {
            BucketName = ElementValue(root, "Bucket") ?? "",
            Key = ElementValue(root, "Key") ?? "",
            UploadId = ElementValue(root, "UploadId") ?? ""
        };

        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "Part"))
        {
            result.Parts.Add(new MultipartPartSummary
            {
                PartNumber = ParseInt(ElementValue(element, "PartNumber")) ?? 0,
                ETag = NormalizeETag(ElementValue(element, "ETag")),
                Size = ParseLong(ElementValue(element, "Size")) ?? 0,
                LastModified = ParseDate(ElementValue(element, "LastModified"))
            });
        }

        return result;
    }

    internal static ListMultipartUploadsResult ParseMultipartUploads(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("ListMultipartUploads response did not contain a root element.");
        var result = new ListMultipartUploadsResult
        {
            BucketName = ElementValue(root, "Bucket") ?? "",
            Prefix = EmptyToNull(ElementValue(root, "Prefix")),
            IsTruncated = string.Equals(ElementValue(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase),
            NextKeyMarker = EmptyToNull(ElementValue(root, "NextKeyMarker")),
            NextUploadIdMarker = EmptyToNull(ElementValue(root, "NextUploadIdMarker"))
        };

        foreach (var element in root.Elements().Where(element => element.Name.LocalName == "Upload"))
        {
            result.Uploads.Add(new MultipartUploadSummary
            {
                Key = ElementValue(element, "Key") ?? "",
                UploadId = ElementValue(element, "UploadId") ?? "",
                Initiated = ParseDate(ElementValue(element, "Initiated"))
            });
        }

        return result;
    }

    internal static string? NormalizeETag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;
    }

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ElementValue(XElement root, string name)
    {
        return root.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
