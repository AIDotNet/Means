namespace Means.Core;

/// <summary>
/// Storage abstraction for the S3-compatible data plane.
/// Implementations own metadata durability, object content durability, and operation atomicity.
/// </summary>
public interface IObjectStore
{
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);

    Task<BucketInfo> CreateBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task<ListObjectsResult> ListObjectsAsync(string bucketName, ListObjectsOptions options, CancellationToken cancellationToken);

    Task<ListObjectVersionsResult> ListObjectVersionsAsync(string bucketName, ListObjectVersionsOptions options, CancellationToken cancellationToken);

    Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken);

    Task<BucketVersioningInfo> GetBucketVersioningAsync(string bucketName, CancellationToken cancellationToken);

    Task<BucketVersioningInfo> PutBucketVersioningAsync(string bucketName, string status, CancellationToken cancellationToken);

    Task<MultipartUploadInfo> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken);

    Task<MultipartPartInfo> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken);

    Task<ObjectInfo> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken);

    Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken);

    Task<ListPartsResult> ListPartsAsync(string bucketName, string key, string uploadId, int partNumberMarker, int maxParts, CancellationToken cancellationToken);

    Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucketName, ListMultipartUploadsOptions options, CancellationToken cancellationToken);

    Task<ObjectData> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<ObjectData> GetObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken);

    Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken);

    Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<DeleteObjectResult> DeleteObjectAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken);

    Task<BatchDeleteResult> DeleteObjectsAsync(string bucketName, IReadOnlyList<BatchDeleteObjectIdentifier> objects, CancellationToken cancellationToken);

    Task<ObjectInfo> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken);

    Task<ObjectTagSet> GetObjectTaggingAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken);

    Task PutObjectTaggingAsync(string bucketName, string key, string? versionId, ObjectTagSet tags, CancellationToken cancellationToken);

    Task DeleteObjectTaggingAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken);

    Task<BucketLifecycleConfiguration?> GetBucketLifecycleAsync(string bucketName, CancellationToken cancellationToken);

    Task PutBucketLifecycleAsync(string bucketName, BucketLifecycleConfiguration configuration, CancellationToken cancellationToken);

    Task DeleteBucketLifecycleAsync(string bucketName, CancellationToken cancellationToken);

    Task<int> ApplyLifecycleRulesAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken);

    Task<BucketCorsConfiguration?> GetBucketCorsAsync(string bucketName, CancellationToken cancellationToken);

    Task PutBucketCorsAsync(string bucketName, BucketCorsConfiguration configuration, CancellationToken cancellationToken);

    Task DeleteBucketCorsAsync(string bucketName, CancellationToken cancellationToken);

    Task<BucketNotificationConfiguration?> GetBucketNotificationAsync(string bucketName, CancellationToken cancellationToken);

    Task PutBucketNotificationAsync(string bucketName, BucketNotificationConfiguration configuration, CancellationToken cancellationToken);

    Task DeleteBucketNotificationAsync(string bucketName, CancellationToken cancellationToken);

    Task<ObjectScrubResult> ScrubObjectReplicasAsync(int maxItems, CancellationToken cancellationToken);
}
