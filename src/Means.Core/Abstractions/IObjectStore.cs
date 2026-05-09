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

    Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken);

    Task<MultipartUploadInfo> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken);

    Task<MultipartPartInfo> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken);

    Task<ObjectInfo> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken);

    Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken);

    Task<ListPartsResult> ListPartsAsync(string bucketName, string key, string uploadId, CancellationToken cancellationToken);

    Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucketName, ListMultipartUploadsOptions options, CancellationToken cancellationToken);

    Task<ObjectData> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<ObjectInfo> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<ObjectInfo> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken);
}
