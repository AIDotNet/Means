namespace Means.Core;

public static class BucketVersioningStatuses
{
    public const string Off = "Off";
    public const string Enabled = "Enabled";
    public const string Suspended = "Suspended";
}

public static class CopyMetadataDirectives
{
    public const string Copy = "COPY";
    public const string Replace = "REPLACE";
}

public sealed record BucketVersioningInfo(string BucketName, string Status);

public sealed record DeleteObjectResult(
    string BucketName,
    string Key,
    string? VersionId,
    bool DeleteMarker);

public sealed record BatchDeleteObjectIdentifier(string Key, string? VersionId = null);

public sealed record BatchDeleteRequest(
    string BucketName,
    IReadOnlyList<BatchDeleteObjectIdentifier> Objects);

public sealed record BatchDeleteError(
    string Key,
    string? VersionId,
    string Code,
    string Message);

public sealed record BatchDeleteResult(
    string BucketName,
    IReadOnlyList<DeleteObjectResult> Deleted,
    IReadOnlyList<BatchDeleteError> Errors);

public sealed record ObjectTagSet(IReadOnlyDictionary<string, string> Tags);

public sealed record BucketLifecycleConfiguration(
    IReadOnlyList<LifecycleRule> Rules);

public sealed record LifecycleRule(
    string Id,
    string Status,
    string Prefix,
    int? ExpirationDays,
    int? NoncurrentVersionExpirationDays,
    int? AbortIncompleteMultipartUploadDays);

public sealed record BucketCorsConfiguration(string Xml);

public sealed record BucketNotificationConfiguration(string Xml);
