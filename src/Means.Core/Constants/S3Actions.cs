namespace Means.Core;

/// <summary>
/// S3-compatible action names used by the policy evaluator and data-plane authorization layer.
/// Keeping the action names centralized prevents policy checks from drifting across endpoint handlers.
/// </summary>
public static class S3Actions
{
    public const string ListAllMyBuckets = "s3:ListAllMyBuckets";
    public const string ListBucket = "s3:ListBucket";
    public const string ListBucketVersions = "s3:ListBucketVersions";
    public const string CreateBucket = "s3:CreateBucket";
    public const string DeleteBucket = "s3:DeleteBucket";
    public const string GetObject = "s3:GetObject";
    public const string PutObject = "s3:PutObject";
    public const string DeleteObject = "s3:DeleteObject";
    public const string GetObjectTagging = "s3:GetObjectTagging";
    public const string PutObjectTagging = "s3:PutObjectTagging";
    public const string DeleteObjectTagging = "s3:DeleteObjectTagging";
    public const string GetBucketVersioning = "s3:GetBucketVersioning";
    public const string PutBucketVersioning = "s3:PutBucketVersioning";
    public const string GetLifecycleConfiguration = "s3:GetLifecycleConfiguration";
    public const string PutLifecycleConfiguration = "s3:PutLifecycleConfiguration";
    public const string GetBucketCORS = "s3:GetBucketCORS";
    public const string PutBucketCORS = "s3:PutBucketCORS";
    public const string GetBucketNotification = "s3:GetBucketNotification";
    public const string PutBucketNotification = "s3:PutBucketNotification";
    public const string AbortMultipartUpload = "s3:AbortMultipartUpload";
    public const string ListMultipartUploadParts = "s3:ListMultipartUploadParts";
}
