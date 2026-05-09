namespace Means.Core;

/// <summary>
/// S3-compatible action names used by the policy evaluator and data-plane authorization layer.
/// Keeping the action names centralized prevents policy checks from drifting across endpoint handlers.
/// </summary>
public static class S3Actions
{
    public const string ListAllMyBuckets = "s3:ListAllMyBuckets";
    public const string ListBucket = "s3:ListBucket";
    public const string CreateBucket = "s3:CreateBucket";
    public const string DeleteBucket = "s3:DeleteBucket";
    public const string GetObject = "s3:GetObject";
    public const string PutObject = "s3:PutObject";
    public const string DeleteObject = "s3:DeleteObject";
    public const string AbortMultipartUpload = "s3:AbortMultipartUpload";
    public const string ListMultipartUploadParts = "s3:ListMultipartUploadParts";
}
