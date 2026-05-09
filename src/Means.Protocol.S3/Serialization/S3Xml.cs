using System.Globalization;
using System.Xml.Linq;
using Means.Core;

namespace Means.Protocol.S3;

/// <summary>
/// Minimal XML serializer for the S3-compatible data plane.
/// SDK generation is driven by SDKs/spec, but the service still returns S3 XML for
/// wire compatibility with existing tooling.
/// </summary>
public static class S3Xml
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static string ListBuckets(IReadOnlyList<BucketInfo> buckets)
    {
        var document = new XDocument(
            new XElement(S3Ns + "ListAllMyBucketsResult",
                new XElement(S3Ns + "Buckets",
                    buckets.Select(bucket =>
                        new XElement(S3Ns + "Bucket",
                            new XElement(S3Ns + "Name", bucket.Name),
                            new XElement(S3Ns + "CreationDate", FormatDate(bucket.CreatedAt)))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ListObjectsV2(ListObjectsResult result)
    {
        var document = new XDocument(
            new XElement(S3Ns + "ListBucketResult",
                new XElement(S3Ns + "Name", result.BucketName),
                new XElement(S3Ns + "Prefix", result.Prefix ?? ""),
                new XElement(S3Ns + "KeyCount", result.KeyCount),
                new XElement(S3Ns + "MaxKeys", result.Objects.Count + result.CommonPrefixes.Count),
                new XElement(S3Ns + "Delimiter", result.Delimiter ?? ""),
                new XElement(S3Ns + "IsTruncated", result.IsTruncated.ToString().ToLowerInvariant()),
                result.NextContinuationToken is null
                    ? null
                    : new XElement(S3Ns + "NextContinuationToken", result.NextContinuationToken),
                result.Objects.Select(item =>
                    new XElement(S3Ns + "Contents",
                        new XElement(S3Ns + "Key", item.Key),
                        new XElement(S3Ns + "LastModified", FormatDate(item.LastModified)),
                        new XElement(S3Ns + "ETag", QuoteEtag(item.ETag)),
                        new XElement(S3Ns + "Size", item.Size),
                        new XElement(S3Ns + "StorageClass", "STANDARD"))),
                result.CommonPrefixes.Select(prefix =>
                    new XElement(S3Ns + "CommonPrefixes", new XElement(S3Ns + "Prefix", prefix)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string CopyObjectResult(ObjectInfo info)
    {
        var document = new XDocument(
            new XElement(S3Ns + "CopyObjectResult",
                new XElement(S3Ns + "LastModified", FormatDate(info.LastModified)),
                new XElement(S3Ns + "ETag", QuoteEtag(info.ETag))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string CopyPartResult(MultipartPartInfo part)
    {
        var document = new XDocument(
            new XElement(S3Ns + "CopyPartResult",
                new XElement(S3Ns + "LastModified", FormatDate(part.LastModified)),
                new XElement(S3Ns + "ETag", QuoteEtag(part.ETag))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string BucketVersioning(BucketVersioningInfo info)
    {
        var document = new XDocument(
            new XElement(S3Ns + "VersioningConfiguration",
                string.Equals(info.Status, BucketVersioningStatuses.Off, StringComparison.Ordinal)
                    ? null
                    : new XElement(S3Ns + "Status", info.Status)));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ObjectTagging(ObjectTagSet tagSet)
    {
        var document = new XDocument(
            new XElement(S3Ns + "Tagging",
                new XElement(S3Ns + "TagSet",
                    tagSet.Tags.Select(tag =>
                        new XElement(S3Ns + "Tag",
                            new XElement(S3Ns + "Key", tag.Key),
                            new XElement(S3Ns + "Value", tag.Value))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string BucketLifecycle(BucketLifecycleConfiguration configuration)
    {
        var document = new XDocument(
            new XElement(S3Ns + "LifecycleConfiguration",
                configuration.Rules.Select(rule =>
                    new XElement(S3Ns + "Rule",
                        new XElement(S3Ns + "ID", rule.Id),
                        new XElement(S3Ns + "Status", rule.Status),
                        new XElement(S3Ns + "Filter", new XElement(S3Ns + "Prefix", rule.Prefix)),
                        rule.ExpirationDays is null
                            ? null
                            : new XElement(S3Ns + "Expiration", new XElement(S3Ns + "Days", rule.ExpirationDays.Value)),
                        rule.NoncurrentVersionExpirationDays is null
                            ? null
                            : new XElement(S3Ns + "NoncurrentVersionExpiration", new XElement(S3Ns + "NoncurrentDays", rule.NoncurrentVersionExpirationDays.Value)),
                        rule.AbortIncompleteMultipartUploadDays is null
                            ? null
                            : new XElement(S3Ns + "AbortIncompleteMultipartUpload", new XElement(S3Ns + "DaysAfterInitiation", rule.AbortIncompleteMultipartUploadDays.Value))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string InitiateMultipartUploadResult(MultipartUploadInfo upload)
    {
        var document = new XDocument(
            new XElement(S3Ns + "InitiateMultipartUploadResult",
                new XElement(S3Ns + "Bucket", upload.BucketName),
                new XElement(S3Ns + "Key", upload.Key),
                new XElement(S3Ns + "UploadId", upload.UploadId)));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string CompleteMultipartUploadResult(ObjectInfo info)
    {
        var document = new XDocument(
            new XElement(S3Ns + "CompleteMultipartUploadResult",
                new XElement(S3Ns + "Location", "/" + info.BucketName + "/" + info.Key),
                new XElement(S3Ns + "Bucket", info.BucketName),
                new XElement(S3Ns + "Key", info.Key),
                new XElement(S3Ns + "ETag", QuoteEtag(info.ETag))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ListParts(ListPartsResult result)
    {
        var document = new XDocument(
            new XElement(S3Ns + "ListPartsResult",
                new XElement(S3Ns + "Bucket", result.BucketName),
                new XElement(S3Ns + "Key", result.Key),
                new XElement(S3Ns + "UploadId", result.UploadId),
                new XElement(S3Ns + "StorageClass", "STANDARD"),
                new XElement(S3Ns + "PartNumberMarker", result.PartNumberMarker),
                new XElement(S3Ns + "NextPartNumberMarker", result.NextPartNumberMarker),
                new XElement(S3Ns + "MaxParts", result.MaxParts),
                new XElement(S3Ns + "IsTruncated", result.IsTruncated.ToString().ToLowerInvariant()),
                result.Parts.Select(part =>
                    new XElement(S3Ns + "Part",
                        new XElement(S3Ns + "PartNumber", part.PartNumber),
                        new XElement(S3Ns + "LastModified", FormatDate(part.LastModified)),
                        new XElement(S3Ns + "ETag", QuoteEtag(part.ETag)),
                        new XElement(S3Ns + "Size", part.Size)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ListMultipartUploads(ListMultipartUploadsResult result)
    {
        var document = new XDocument(
            new XElement(S3Ns + "ListMultipartUploadsResult",
                new XElement(S3Ns + "Bucket", result.BucketName),
                new XElement(S3Ns + "KeyMarker", result.KeyMarker ?? ""),
                new XElement(S3Ns + "UploadIdMarker", result.UploadIdMarker ?? ""),
                result.NextKeyMarker is null ? null : new XElement(S3Ns + "NextKeyMarker", result.NextKeyMarker),
                result.NextUploadIdMarker is null ? null : new XElement(S3Ns + "NextUploadIdMarker", result.NextUploadIdMarker),
                new XElement(S3Ns + "Prefix", result.Prefix ?? ""),
                new XElement(S3Ns + "Delimiter", result.Delimiter ?? ""),
                new XElement(S3Ns + "MaxUploads", result.MaxUploads),
                new XElement(S3Ns + "IsTruncated", result.IsTruncated.ToString().ToLowerInvariant()),
                result.Uploads.Select(upload =>
                    new XElement(S3Ns + "Upload",
                        new XElement(S3Ns + "Key", upload.Key),
                        new XElement(S3Ns + "UploadId", upload.UploadId),
                        new XElement(S3Ns + "StorageClass", "STANDARD"),
                        new XElement(S3Ns + "Initiated", FormatDate(upload.InitiatedAt)))),
                result.CommonPrefixes.Select(prefix =>
                    new XElement(S3Ns + "CommonPrefixes", new XElement(S3Ns + "Prefix", prefix)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ListObjectVersions(ListObjectVersionsResult result)
    {
        var document = new XDocument(
            new XElement(S3Ns + "ListVersionsResult",
                new XElement(S3Ns + "Name", result.BucketName),
                new XElement(S3Ns + "Prefix", result.Prefix ?? ""),
                new XElement(S3Ns + "KeyMarker", result.KeyMarker ?? ""),
                new XElement(S3Ns + "VersionIdMarker", result.VersionIdMarker ?? ""),
                result.NextKeyMarker is null ? null : new XElement(S3Ns + "NextKeyMarker", result.NextKeyMarker),
                result.NextVersionIdMarker is null ? null : new XElement(S3Ns + "NextVersionIdMarker", result.NextVersionIdMarker),
                new XElement(S3Ns + "MaxKeys", result.MaxKeys),
                new XElement(S3Ns + "Delimiter", result.Delimiter ?? ""),
                new XElement(S3Ns + "IsTruncated", result.IsTruncated.ToString().ToLowerInvariant()),
                result.Versions.Select(version =>
                    version.IsDeleteMarker
                        ? new XElement(S3Ns + "DeleteMarker",
                            new XElement(S3Ns + "Key", version.Key),
                            new XElement(S3Ns + "VersionId", version.VersionId),
                            new XElement(S3Ns + "IsLatest", version.IsLatest.ToString().ToLowerInvariant()),
                            new XElement(S3Ns + "LastModified", FormatDate(version.LastModified)))
                        : new XElement(S3Ns + "Version",
                            new XElement(S3Ns + "Key", version.Key),
                            new XElement(S3Ns + "VersionId", version.VersionId),
                            new XElement(S3Ns + "IsLatest", version.IsLatest.ToString().ToLowerInvariant()),
                            new XElement(S3Ns + "LastModified", FormatDate(version.LastModified)),
                            new XElement(S3Ns + "ETag", QuoteEtag(version.ETag)),
                            new XElement(S3Ns + "Size", version.Size),
                            new XElement(S3Ns + "StorageClass", "STANDARD"))),
                result.CommonPrefixes.Select(prefix =>
                    new XElement(S3Ns + "CommonPrefixes", new XElement(S3Ns + "Prefix", prefix)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string Error(string code, string message, string? resource, string requestId)
    {
        var document = new XDocument(
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", message),
                resource is null ? null : new XElement("Resource", resource),
                new XElement("RequestId", requestId)));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string QuoteEtag(string etag)
    {
        return etag.StartsWith('"') ? etag : $"\"{etag}\"";
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
