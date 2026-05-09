namespace Means.Core;

/// <summary>
/// S3-style XML error codes returned by the HTTP data plane and mapped by official SDKs.
/// </summary>
public static class MeansErrorCodes
{
    public const string AccessDenied = "AccessDenied";
    public const string BucketAlreadyExists = "BucketAlreadyExists";
    public const string BucketNotEmpty = "BucketNotEmpty";
    public const string EntityTooLarge = "EntityTooLarge";
    public const string EntityTooSmall = "EntityTooSmall";
    public const string InvalidArgument = "InvalidArgument";
    public const string InvalidPart = "InvalidPart";
    public const string InvalidPartOrder = "InvalidPartOrder";
    public const string InvalidRange = "InvalidRange";
    public const string InvalidRequest = "InvalidRequest";
    public const string InvalidTag = "InvalidTag";
    public const string MalformedXML = "MalformedXML";
    public const string NoSuchCORSConfiguration = "NoSuchCORSConfiguration";
    public const string NoSuchBucket = "NoSuchBucket";
    public const string NoSuchKey = "NoSuchKey";
    public const string NoSuchLifecycleConfiguration = "NoSuchLifecycleConfiguration";
    public const string NoSuchUpload = "NoSuchUpload";
    public const string NoSuchVersion = "NoSuchVersion";
    public const string NotModified = "NotModified";
    public const string PreconditionFailed = "PreconditionFailed";
    public const string SignatureDoesNotMatch = "SignatureDoesNotMatch";
    public const string SlowDown = "SlowDown";
}
