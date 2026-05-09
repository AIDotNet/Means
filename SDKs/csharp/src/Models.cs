using System.Net;

namespace Means;

/// <summary>
/// Bucket list entry.
/// </summary>
public sealed class BucketSummary
{
    public BucketSummary()
    {
    }

    public BucketSummary(string name, DateTimeOffset? creationDate = null)
    {
        Name = name;
        CreationDate = creationDate;
    }

    public string Name { get; set; } = "";

    public DateTimeOffset? CreationDate { get; set; }
}

/// <summary>
/// Object list entry.
/// </summary>
public sealed class ObjectSummary
{
    public ObjectSummary()
    {
    }

    public ObjectSummary(string key, long size, string? eTag = null, DateTimeOffset? lastModified = null)
    {
        Key = key;
        Size = size;
        ETag = eTag;
        LastModified = lastModified;
    }

    public string Key { get; set; } = "";

    public string? ETag { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public string? StorageClass { get; set; }

    public string? ContentType { get; set; }
}

/// <summary>
/// Paged ListObjectsV2 result.
/// </summary>
public sealed class ListObjectsResult
{
    public string BucketName { get; set; } = "";

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public int KeyCount { get; set; }

    public int? MaxKeys { get; set; }

    public bool IsTruncated { get; set; }

    public string? NextContinuationToken { get; set; }

    public List<ObjectSummary> Objects { get; set; } = new();

    public List<string> CommonPrefixes { get; set; } = new();
}

public sealed class BucketVersioningResult
{
    public string BucketName { get; set; } = "";

    public string Status { get; set; } = "";

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

public sealed class ObjectVersionSummary
{
    public string Key { get; set; } = "";

    public string VersionId { get; set; } = "";

    public bool IsLatest { get; set; }

    public bool IsDeleteMarker { get; set; }

    public string? ETag { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? LastModified { get; set; }
}

public sealed class ListObjectVersionsResult
{
    public string BucketName { get; set; } = "";

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public bool IsTruncated { get; set; }

    public string? NextKeyMarker { get; set; }

    public string? NextVersionIdMarker { get; set; }

    public List<ObjectVersionSummary> Versions { get; set; } = new();

    public List<string> CommonPrefixes { get; set; } = new();

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

public sealed class BucketLifecycleConfiguration
{
    public List<LifecycleRule> Rules { get; set; } = new();
}

public sealed class LifecycleRule
{
    public string Id { get; set; } = "";

    public string Status { get; set; } = "Enabled";

    public string Prefix { get; set; } = "";

    public int? ExpirationDays { get; set; }

    public int? NoncurrentVersionExpirationDays { get; set; }

    public int? AbortIncompleteMultipartUploadDays { get; set; }
}

public sealed class BucketLifecycleResult
{
    public string BucketName { get; set; } = "";

    public BucketLifecycleConfiguration Configuration { get; set; } = new();

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

public sealed class BucketXmlConfigurationResult
{
    public string BucketName { get; set; } = "";

    public string Xml { get; set; } = "";

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

public sealed class ObjectTaggingResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string? VersionId { get; set; }

    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Metadata returned by HEAD object and GET object.
/// </summary>
public class ObjectHeadResult
{
    public string? BucketName { get; set; }

    public string? Key { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? ETag { get; set; }

    public long? ContentLength { get; set; }

    public string? ContentType { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public string? CacheControl { get; set; }

    public string? ContentDisposition { get; set; }

    public string? ContentEncoding { get; set; }

    public string? AcceptRanges { get; set; }

    public string? VersionId { get; set; }

    public string? RequestId { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Stream and metadata returned by GET object.
/// </summary>
public sealed class GetObjectResult : IDisposable, IAsyncDisposable
{
    private readonly IDisposable? _owner;

    internal GetObjectResult(Stream content, ObjectHeadResult head, IDisposable? owner)
    {
        Content = content;
        Head = head;
        _owner = owner;
    }

    public Stream Content { get; }

    public ObjectHeadResult Head { get; }

    public string? ETag => Head.ETag;

    public long? ContentLength => Head.ContentLength;

    public string? ContentType => Head.ContentType;

    public DateTimeOffset? LastModified => Head.LastModified;

    public Dictionary<string, string> Metadata => Head.Metadata;

    public void Dispose()
    {
        Content.Dispose();
        _owner?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Content.Dispose();
        _owner?.Dispose();
        return default;
    }
}

/// <summary>
/// Result returned after PUT object.
/// </summary>
public sealed class PutObjectResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public HttpStatusCode StatusCode { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public string? VersionId { get; set; }

    public string? RequestId { get; set; }
}

public sealed class DeleteObjectResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string? VersionId { get; set; }

    public bool DeleteMarker { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Result returned after server-side object copy.
/// </summary>
public sealed class CopyObjectResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string SourceBucketName { get; set; } = "";

    public string SourceKey { get; set; } = "";

    public string? SourceVersionId { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public string? VersionId { get; set; }

    public string? RequestId { get; set; }
}

public sealed class CopyObjectOptions
{
    public string SourceBucketName { get; set; } = "";

    public string SourceKey { get; set; } = "";

    public string? SourceVersionId { get; set; }

    public string DestinationBucketName { get; set; } = "";

    public string DestinationKey { get; set; } = "";

    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    public string? MetadataDirective { get; set; }

    public string? ContentType { get; set; }

    public string? CacheControl { get; set; }

    public string? ContentDisposition { get; set; }
}

/// <summary>
/// Multipart upload session returned after CreateMultipartUpload.
/// </summary>
public sealed class MultipartUploadResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Result returned after uploading one multipart part.
/// </summary>
public sealed class UploadPartResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public int PartNumber { get; set; }

    public string? ETag { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Options for server-side UploadPartCopy.
/// </summary>
public sealed class UploadPartCopyOptions
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public int PartNumber { get; set; }

    public string SourceBucketName { get; set; } = "";

    public string SourceKey { get; set; } = "";

    public string? SourceVersionId { get; set; }

    public string? CopySourceRange { get; set; }
}

/// <summary>
/// Result returned after server-side UploadPartCopy.
/// </summary>
public sealed class CopyPartResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public int PartNumber { get; set; }

    public string SourceBucketName { get; set; } = "";

    public string SourceKey { get; set; } = "";

    public string? SourceVersionId { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Part reference supplied to CompleteMultipartUpload.
/// </summary>
public sealed class CompletedMultipartPart
{
    public CompletedMultipartPart()
    {
    }

    public CompletedMultipartPart(int partNumber, string eTag)
    {
        PartNumber = partNumber;
        ETag = eTag;
    }

    public int PartNumber { get; set; }

    public string ETag { get; set; } = "";
}

/// <summary>
/// Result returned after completing a multipart upload.
/// </summary>
public sealed class CompleteMultipartUploadResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string? Location { get; set; }

    public string? ETag { get; set; }

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// Multipart part list entry.
/// </summary>
public sealed class MultipartPartSummary
{
    public int PartNumber { get; set; }

    public string? ETag { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>
/// Paged ListParts result.
/// </summary>
public sealed class ListPartsResult
{
    public string BucketName { get; set; } = "";

    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public DateTimeOffset? Initiated { get; set; }

    public int PartNumberMarker { get; set; }

    public int NextPartNumberMarker { get; set; }

    public int MaxParts { get; set; }

    public bool IsTruncated { get; set; }

    public List<MultipartPartSummary> Parts { get; set; } = new();

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>
/// In-progress multipart upload list entry.
/// </summary>
public sealed class MultipartUploadSummary
{
    public string Key { get; set; } = "";

    public string UploadId { get; set; } = "";

    public DateTimeOffset? Initiated { get; set; }
}

/// <summary>
/// Paged ListMultipartUploads result.
/// </summary>
public sealed class ListMultipartUploadsResult
{
    public string BucketName { get; set; } = "";

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public string? KeyMarker { get; set; }

    public string? UploadIdMarker { get; set; }

    public int MaxUploads { get; set; }

    public bool IsTruncated { get; set; }

    public string? NextKeyMarker { get; set; }

    public string? NextUploadIdMarker { get; set; }

    public List<MultipartUploadSummary> Uploads { get; set; } = new();

    public List<string> CommonPrefixes { get; set; } = new();

    public HttpStatusCode StatusCode { get; set; }

    public string? RequestId { get; set; }
}

public sealed class ListMultipartUploadsOptions
{
    public string BucketName { get; set; } = "";

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public string? KeyMarker { get; set; }

    public string? UploadIdMarker { get; set; }

    public int? MaxUploads { get; set; }
}

/// <summary>
/// SigV4 presigned request information.
/// </summary>
public sealed class PresignedRequest
{
    public PresignedRequest()
    {
    }

    public PresignedRequest(Uri url, HttpMethod method, DateTimeOffset expiresAt)
    {
        Url = url;
        Method = method;
        ExpiresAt = expiresAt;
    }

    public Uri Url { get; set; } = new("about:blank");

    public HttpMethod Method { get; set; } = HttpMethod.Get;

    public DateTimeOffset ExpiresAt { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString()
    {
        return Url.ToString();
    }
}
