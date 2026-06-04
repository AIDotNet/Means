using System.Globalization;
using System.Text;
using Means.Core;

namespace Means.Protocol.S3;

/// <summary>
/// Minimal XML serializer for the S3-compatible data plane.
/// Kept hand-written so Native AOT does not need to carry LINQ-to-XML.
/// </summary>
public static class S3Xml
{
    private const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static string ListBuckets(IReadOnlyList<BucketInfo> buckets)
    {
        var xml = BeginS3("ListAllMyBucketsResult");
        Open(xml, "Buckets");
        foreach (var bucket in buckets)
        {
            Open(xml, "Bucket");
            Element(xml, "Name", bucket.Name);
            Element(xml, "CreationDate", FormatDate(bucket.CreatedAt));
            Close(xml, "Bucket");
        }

        Close(xml, "Buckets");
        Close(xml, "ListAllMyBucketsResult");
        return xml.ToString();
    }

    public static string ListObjectsV2(ListObjectsResult result)
    {
        var xml = BeginS3("ListBucketResult");
        Element(xml, "Name", result.BucketName);
        Element(xml, "Prefix", result.Prefix);
        Element(xml, "KeyCount", result.KeyCount);
        Element(xml, "MaxKeys", result.Objects.Count + result.CommonPrefixes.Count);
        Element(xml, "Delimiter", result.Delimiter);
        Element(xml, "IsTruncated", result.IsTruncated);
        if (result.NextContinuationToken is not null)
        {
            Element(xml, "NextContinuationToken", result.NextContinuationToken);
        }

        foreach (var item in result.Objects)
        {
            Open(xml, "Contents");
            Element(xml, "Key", item.Key);
            Element(xml, "LastModified", FormatDate(item.LastModified));
            Element(xml, "ETag", QuoteEtag(item.ETag));
            Element(xml, "Size", item.Size);
            Element(xml, "StorageClass", "STANDARD");
            Close(xml, "Contents");
        }

        AppendCommonPrefixes(xml, result.CommonPrefixes);
        Close(xml, "ListBucketResult");
        return xml.ToString();
    }

    public static string CopyObjectResult(ObjectInfo info)
    {
        var xml = BeginS3("CopyObjectResult");
        Element(xml, "LastModified", FormatDate(info.LastModified));
        Element(xml, "ETag", QuoteEtag(info.ETag));
        Close(xml, "CopyObjectResult");
        return xml.ToString();
    }

    public static string CopyPartResult(MultipartPartInfo part)
    {
        var xml = BeginS3("CopyPartResult");
        Element(xml, "LastModified", FormatDate(part.LastModified));
        Element(xml, "ETag", QuoteEtag(part.ETag));
        Close(xml, "CopyPartResult");
        return xml.ToString();
    }

    public static string BucketVersioning(BucketVersioningInfo info)
    {
        var xml = BeginS3("VersioningConfiguration");
        if (!string.Equals(info.Status, BucketVersioningStatuses.Off, StringComparison.Ordinal))
        {
            Element(xml, "Status", info.Status);
        }

        Close(xml, "VersioningConfiguration");
        return xml.ToString();
    }

    public static string ObjectTagging(ObjectTagSet tagSet)
    {
        var xml = BeginS3("Tagging");
        Open(xml, "TagSet");
        foreach (var tag in tagSet.Tags)
        {
            Open(xml, "Tag");
            Element(xml, "Key", tag.Key);
            Element(xml, "Value", tag.Value);
            Close(xml, "Tag");
        }

        Close(xml, "TagSet");
        Close(xml, "Tagging");
        return xml.ToString();
    }

    public static string BucketLifecycle(BucketLifecycleConfiguration configuration)
    {
        var xml = BeginS3("LifecycleConfiguration");
        foreach (var rule in configuration.Rules)
        {
            Open(xml, "Rule");
            Element(xml, "ID", rule.Id);
            Element(xml, "Status", rule.Status);
            Open(xml, "Filter");
            Element(xml, "Prefix", rule.Prefix);
            Close(xml, "Filter");
            if (rule.ExpirationDays is int expirationDays)
            {
                Open(xml, "Expiration");
                Element(xml, "Days", expirationDays);
                Close(xml, "Expiration");
            }

            if (rule.NoncurrentVersionExpirationDays is int noncurrentDays)
            {
                Open(xml, "NoncurrentVersionExpiration");
                Element(xml, "NoncurrentDays", noncurrentDays);
                Close(xml, "NoncurrentVersionExpiration");
            }

            if (rule.AbortIncompleteMultipartUploadDays is int abortDays)
            {
                Open(xml, "AbortIncompleteMultipartUpload");
                Element(xml, "DaysAfterInitiation", abortDays);
                Close(xml, "AbortIncompleteMultipartUpload");
            }

            Close(xml, "Rule");
        }

        Close(xml, "LifecycleConfiguration");
        return xml.ToString();
    }

    public static string InitiateMultipartUploadResult(MultipartUploadInfo upload)
    {
        var xml = BeginS3("InitiateMultipartUploadResult");
        Element(xml, "Bucket", upload.BucketName);
        Element(xml, "Key", upload.Key);
        Element(xml, "UploadId", upload.UploadId);
        Close(xml, "InitiateMultipartUploadResult");
        return xml.ToString();
    }

    public static string CompleteMultipartUploadResult(ObjectInfo info)
    {
        var xml = BeginS3("CompleteMultipartUploadResult");
        Element(xml, "Location", "/" + info.BucketName + "/" + info.Key);
        Element(xml, "Bucket", info.BucketName);
        Element(xml, "Key", info.Key);
        Element(xml, "ETag", QuoteEtag(info.ETag));
        Close(xml, "CompleteMultipartUploadResult");
        return xml.ToString();
    }

    public static string ListParts(ListPartsResult result)
    {
        var xml = BeginS3("ListPartsResult");
        Element(xml, "Bucket", result.BucketName);
        Element(xml, "Key", result.Key);
        Element(xml, "UploadId", result.UploadId);
        Element(xml, "StorageClass", "STANDARD");
        Element(xml, "PartNumberMarker", result.PartNumberMarker);
        Element(xml, "NextPartNumberMarker", result.NextPartNumberMarker);
        Element(xml, "MaxParts", result.MaxParts);
        Element(xml, "IsTruncated", result.IsTruncated);
        foreach (var part in result.Parts)
        {
            Open(xml, "Part");
            Element(xml, "PartNumber", part.PartNumber);
            Element(xml, "LastModified", FormatDate(part.LastModified));
            Element(xml, "ETag", QuoteEtag(part.ETag));
            Element(xml, "Size", part.Size);
            Close(xml, "Part");
        }

        Close(xml, "ListPartsResult");
        return xml.ToString();
    }

    public static string ListMultipartUploads(ListMultipartUploadsResult result)
    {
        var xml = BeginS3("ListMultipartUploadsResult");
        Element(xml, "Bucket", result.BucketName);
        Element(xml, "KeyMarker", result.KeyMarker);
        Element(xml, "UploadIdMarker", result.UploadIdMarker);
        if (result.NextKeyMarker is not null)
        {
            Element(xml, "NextKeyMarker", result.NextKeyMarker);
        }

        if (result.NextUploadIdMarker is not null)
        {
            Element(xml, "NextUploadIdMarker", result.NextUploadIdMarker);
        }

        Element(xml, "Prefix", result.Prefix);
        Element(xml, "Delimiter", result.Delimiter);
        Element(xml, "MaxUploads", result.MaxUploads);
        Element(xml, "IsTruncated", result.IsTruncated);
        foreach (var upload in result.Uploads)
        {
            Open(xml, "Upload");
            Element(xml, "Key", upload.Key);
            Element(xml, "UploadId", upload.UploadId);
            Element(xml, "StorageClass", "STANDARD");
            Element(xml, "Initiated", FormatDate(upload.InitiatedAt));
            Close(xml, "Upload");
        }

        AppendCommonPrefixes(xml, result.CommonPrefixes);
        Close(xml, "ListMultipartUploadsResult");
        return xml.ToString();
    }

    public static string ListObjectVersions(ListObjectVersionsResult result)
    {
        var xml = BeginS3("ListVersionsResult");
        Element(xml, "Name", result.BucketName);
        Element(xml, "Prefix", result.Prefix);
        Element(xml, "KeyMarker", result.KeyMarker);
        Element(xml, "VersionIdMarker", result.VersionIdMarker);
        if (result.NextKeyMarker is not null)
        {
            Element(xml, "NextKeyMarker", result.NextKeyMarker);
        }

        if (result.NextVersionIdMarker is not null)
        {
            Element(xml, "NextVersionIdMarker", result.NextVersionIdMarker);
        }

        Element(xml, "MaxKeys", result.MaxKeys);
        Element(xml, "Delimiter", result.Delimiter);
        Element(xml, "IsTruncated", result.IsTruncated);
        foreach (var version in result.Versions)
        {
            if (version.IsDeleteMarker)
            {
                Open(xml, "DeleteMarker");
                Element(xml, "Key", version.Key);
                Element(xml, "VersionId", version.VersionId);
                Element(xml, "IsLatest", version.IsLatest);
                Element(xml, "LastModified", FormatDate(version.LastModified));
                Close(xml, "DeleteMarker");
                continue;
            }

            Open(xml, "Version");
            Element(xml, "Key", version.Key);
            Element(xml, "VersionId", version.VersionId);
            Element(xml, "IsLatest", version.IsLatest);
            Element(xml, "LastModified", FormatDate(version.LastModified));
            Element(xml, "ETag", QuoteEtag(version.ETag));
            Element(xml, "Size", version.Size);
            Element(xml, "StorageClass", "STANDARD");
            Close(xml, "Version");
        }

        AppendCommonPrefixes(xml, result.CommonPrefixes);
        Close(xml, "ListVersionsResult");
        return xml.ToString();
    }

    public static string Error(string code, string message, string? resource, string requestId)
    {
        var xml = new StringBuilder(256);
        Open(xml, "Error");
        Element(xml, "Code", code);
        Element(xml, "Message", message);
        if (resource is not null)
        {
            Element(xml, "Resource", resource);
        }

        Element(xml, "RequestId", requestId);
        Close(xml, "Error");
        return xml.ToString();
    }

    public static string QuoteEtag(string etag)
    {
        return etag.StartsWith('"') ? etag : $"\"{etag}\"";
    }

    private static StringBuilder BeginS3(string root)
    {
        var xml = new StringBuilder(512);
        xml.Append('<').Append(root).Append(" xmlns=\"").Append(S3Namespace).Append("\">");
        return xml;
    }

    private static void AppendCommonPrefixes(StringBuilder xml, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            Open(xml, "CommonPrefixes");
            Element(xml, "Prefix", prefix);
            Close(xml, "CommonPrefixes");
        }
    }

    private static void Open(StringBuilder xml, string name)
    {
        xml.Append('<').Append(name).Append('>');
    }

    private static void Close(StringBuilder xml, string name)
    {
        xml.Append("</").Append(name).Append('>');
    }

    private static void Element(StringBuilder xml, string name, string? value)
    {
        Open(xml, name);
        AppendEscaped(xml, value ?? string.Empty);
        Close(xml, name);
    }

    private static void Element(StringBuilder xml, string name, int value)
    {
        Element(xml, name, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Element(StringBuilder xml, string name, long value)
    {
        Element(xml, name, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Element(StringBuilder xml, string name, bool value)
    {
        Element(xml, name, value ? "true" : "false");
    }

    private static void AppendEscaped(StringBuilder xml, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    xml.Append("&amp;");
                    break;
                case '<':
                    xml.Append("&lt;");
                    break;
                case '>':
                    xml.Append("&gt;");
                    break;
                default:
                    xml.Append(character);
                    break;
            }
        }
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
