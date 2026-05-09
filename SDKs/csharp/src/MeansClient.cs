using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Means.Internal;

namespace Means;

/// <summary>
/// C# client for the Means S3-compatible object storage API.
/// </summary>
public sealed class MeansClient : IDisposable
{
    private const int DefaultMultipartPartSize = 16 * 1024 * 1024;
    private const int MinimumMultipartPartSize = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly MeansClientOptions _options;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a client with an internally managed <see cref="HttpClient"/>.
    /// </summary>
    public MeansClient(MeansClientOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Clone();
        _options.Validate();
        _httpClient = new HttpClient();
        if (_options.Timeout is not null)
        {
            _httpClient.Timeout = _options.Timeout.Value;
        }

        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a client with a caller-managed <see cref="HttpClient"/>.
    /// </summary>
    public MeansClient(HttpClient httpClient, MeansClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Clone();
        _options.Validate();
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Lists buckets visible to the configured access key.
    /// </summary>
    public async Task<IReadOnlyList<BucketSummary>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, bucketName: null, key: null);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return S3XmlParser.ParseBuckets(body);
    }

    /// <summary>
    /// Creates a bucket.
    /// </summary>
    public async Task<BucketSummary> CreateBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        using var request = CreateRequest(HttpMethod.Put, bucketName, key: null);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return new BucketSummary(bucketName, response.Headers.Date);
    }

    /// <summary>
    /// Checks that a bucket exists and is accessible. Throws <see cref="MeansError"/> on S3-style errors.
    /// </summary>
    public async Task HeadBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        using var request = CreateRequest(HttpMethod.Head, bucketName, key: null);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a bucket.
    /// </summary>
    public async Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        using var request = CreateRequest(HttpMethod.Delete, bucketName, key: null);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists objects using the S3 ListObjectsV2 API.
    /// </summary>
    public async Task<ListObjectsResult> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        string? delimiter = null,
        string? continuationToken = null,
        int? maxKeys = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        if (maxKeys is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "Max keys must be greater than zero.");
        }

        var query = new List<KeyValuePair<string, string>>
        {
            new("list-type", "2")
        };

        AddQuery(query, "prefix", prefix);
        AddQuery(query, "delimiter", delimiter);
        AddQuery(query, "continuation-token", continuationToken);
        if (maxKeys is not null)
        {
            AddQuery(query, "max-keys", maxKeys.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var request = CreateRequest(HttpMethod.Get, bucketName, key: null, query);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return S3XmlParser.ParseObjects(body);
    }

    /// <summary>
    /// Uploads an object stream.
    /// </summary>
    public async Task<PutObjectResult> PutObjectAsync(
        string bucketName,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? cacheControl = null,
        string? contentDisposition = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        using var request = CreateRequest(HttpMethod.Put, bucketName, key);
        request.Content = new StreamContent(content);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            request.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
        }

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(contentDisposition);
        }

        AddMetadataHeaders(request, metadata);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return new PutObjectResult
        {
            BucketName = bucketName,
            Key = key,
            StatusCode = response.StatusCode,
            ETag = S3XmlParser.NormalizeETag(HeaderValue(response, "ETag")),
            LastModified = response.Content.Headers.LastModified ?? response.Headers.Date,
            VersionId = HeaderValue(response, "x-amz-version-id"),
            RequestId = HeaderValue(response, "x-amz-request-id")
        };
    }

    /// <summary>
    /// Downloads an object as a stream. Dispose the returned result to release the response connection.
    /// </summary>
    public async Task<GetObjectResult> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);

        var request = CreateRequest(HttpMethod.Get, bucketName, key);
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        request.Dispose();

        try
        {
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var head = ReadObjectHeaders(response, bucketName, key);
            return new GetObjectResult(stream, head, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Downloads an object into a destination stream and returns its metadata.
    /// </summary>
    public async Task<ObjectHeadResult> GetObjectAsync(
        string bucketName,
        string key,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        await using var result = await GetObjectAsync(bucketName, key, cancellationToken).ConfigureAwait(false);
        await result.Content.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
        return result.Head;
    }

    /// <summary>
    /// Reads object metadata without returning the object body.
    /// </summary>
    public async Task<ObjectHeadResult> HeadObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        using var request = CreateRequest(HttpMethod.Head, bucketName, key);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return ReadObjectHeaders(response, bucketName, key);
    }

    /// <summary>
    /// Deletes an object.
    /// </summary>
    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        using var request = CreateRequest(HttpMethod.Delete, bucketName, key);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies an object on the server using the S3 x-amz-copy-source header.
    /// </summary>
    public async Task<CopyObjectResult> CopyObjectAsync(
        string sourceBucketName,
        string sourceKey,
        string destinationBucketName,
        string destinationKey,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? cacheControl = null,
        string? contentDisposition = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(sourceBucketName);
        ValidateBucketName(destinationBucketName);
        ValidateObjectKey(sourceKey);
        ValidateObjectKey(destinationKey);

        using var request = CreateRequest(HttpMethod.Put, destinationBucketName, destinationKey);
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", "/" + EscapePathSegment(sourceBucketName) + "/" + EscapeKey(sourceKey));
        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            request.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        }

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.Headers.TryAddWithoutValidation("Content-Disposition", contentDisposition);
        }

        AddMetadataHeaders(request, metadata);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var result = string.IsNullOrWhiteSpace(body)
            ? new CopyObjectResult()
            : S3XmlParser.ParseCopyObject(body);
        result.BucketName = destinationBucketName;
        result.Key = destinationKey;
        result.StatusCode = response.StatusCode;
        result.RequestId = HeaderValue(response, "x-amz-request-id");
        result.VersionId = HeaderValue(response, "x-amz-version-id");
        return result;
    }

    /// <summary>
    /// Starts a multipart upload and captures the metadata that will be applied at completion.
    /// </summary>
    public async Task<MultipartUploadResult> InitiateMultipartUploadAsync(
        string bucketName,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? cacheControl = null,
        string? contentDisposition = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);

        using var request = CreateRequest(
            HttpMethod.Post,
            bucketName,
            key,
            new[] { new KeyValuePair<string, string>("uploads", "") });
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            request.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
        }

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(contentDisposition);
        }

        AddMetadataHeaders(request, metadata);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var result = S3XmlParser.ParseInitiateMultipartUpload(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        result.StatusCode = response.StatusCode;
        result.RequestId = HeaderValue(response, "x-amz-request-id");
        return result;
    }

    /// <summary>
    /// Uploads one multipart part.
    /// </summary>
    public async Task<UploadPartResult> UploadPartAsync(
        string bucketName,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        ValidateUploadId(uploadId);
        ValidatePartNumber(partNumber);
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        using var request = CreateRequest(
            HttpMethod.Put,
            bucketName,
            key,
            new[]
            {
                new KeyValuePair<string, string>("partNumber", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("uploadId", uploadId)
            });
        request.Content = new StreamContent(content);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return new UploadPartResult
        {
            BucketName = bucketName,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            ETag = S3XmlParser.NormalizeETag(HeaderValue(response, "ETag")),
            StatusCode = response.StatusCode,
            RequestId = HeaderValue(response, "x-amz-request-id")
        };
    }

    /// <summary>
    /// Completes a multipart upload using the uploaded part numbers and ETags.
    /// </summary>
    public async Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        IReadOnlyList<CompletedMultipartPart> parts,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        ValidateUploadId(uploadId);
        ValidateCompletedParts(parts);

        using var request = CreateRequest(
            HttpMethod.Post,
            bucketName,
            key,
            new[] { new KeyValuePair<string, string>("uploadId", uploadId) });
        request.Content = new StringContent(BuildCompleteMultipartXml(parts), Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var result = S3XmlParser.ParseCompleteMultipartUpload(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        result.StatusCode = response.StatusCode;
        result.RequestId = HeaderValue(response, "x-amz-request-id");
        return result;
    }

    /// <summary>
    /// Aborts an in-progress multipart upload and deletes uploaded parts.
    /// </summary>
    public async Task AbortMultipartUploadAsync(
        string bucketName,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        ValidateUploadId(uploadId);
        using var request = CreateRequest(
            HttpMethod.Delete,
            bucketName,
            key,
            new[] { new KeyValuePair<string, string>("uploadId", uploadId) });
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists uploaded parts for an in-progress multipart upload.
    /// </summary>
    public async Task<ListPartsResult> ListPartsAsync(
        string bucketName,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        ValidateUploadId(uploadId);
        using var request = CreateRequest(
            HttpMethod.Get,
            bucketName,
            key,
            new[] { new KeyValuePair<string, string>("uploadId", uploadId) });
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var result = S3XmlParser.ParseParts(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        result.StatusCode = response.StatusCode;
        result.RequestId = HeaderValue(response, "x-amz-request-id");
        return result;
    }

    /// <summary>
    /// Lists in-progress multipart uploads in a bucket.
    /// </summary>
    public async Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(
        string bucketName,
        string? prefix = null,
        string? keyMarker = null,
        string? uploadIdMarker = null,
        int? maxUploads = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        if (maxUploads is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUploads), "Max uploads must be greater than zero.");
        }

        var query = new List<KeyValuePair<string, string>>
        {
            new("uploads", "")
        };
        AddQuery(query, "prefix", prefix);
        AddQuery(query, "key-marker", keyMarker);
        AddQuery(query, "upload-id-marker", uploadIdMarker);
        if (maxUploads is not null)
        {
            AddQuery(query, "max-uploads", maxUploads.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var request = CreateRequest(HttpMethod.Get, bucketName, key: null, query);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var result = S3XmlParser.ParseMultipartUploads(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        result.StatusCode = response.StatusCode;
        result.RequestId = HeaderValue(response, "x-amz-request-id");
        return result;
    }

    /// <summary>
    /// Uploads a seekable stream using multipart upload. Parts are uploaded sequentially.
    /// </summary>
    public async Task<CompleteMultipartUploadResult> UploadObjectMultipartAsync(
        string bucketName,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? cacheControl = null,
        string? contentDisposition = null,
        int partSize = DefaultMultipartPartSize,
        CancellationToken cancellationToken = default)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("Multipart helper requires a readable, seekable stream.", nameof(content));
        }

        if (partSize < MinimumMultipartPartSize)
        {
            throw new ArgumentOutOfRangeException(nameof(partSize), "Part size must be at least 5 MiB.");
        }

        var upload = await InitiateMultipartUploadAsync(bucketName, key, contentType, metadata, cacheControl, contentDisposition, cancellationToken).ConfigureAwait(false);
        var completedParts = new List<CompletedMultipartPart>();
        try
        {
            var partNumber = 1;
            while (content.Position < content.Length)
            {
                using var buffer = await ReadPartAsync(content, partSize, cancellationToken).ConfigureAwait(false);
                var part = await UploadPartAsync(bucketName, key, upload.UploadId, partNumber, buffer, cancellationToken).ConfigureAwait(false);
                completedParts.Add(new CompletedMultipartPart(partNumber, part.ETag ?? ""));
                partNumber++;
            }

            if (completedParts.Count == 0)
            {
                using var empty = new MemoryStream(Array.Empty<byte>());
                var part = await UploadPartAsync(bucketName, key, upload.UploadId, 1, empty, cancellationToken).ConfigureAwait(false);
                completedParts.Add(new CompletedMultipartPart(1, part.ETag ?? ""));
            }

            return await CompleteMultipartUploadAsync(bucketName, key, upload.UploadId, completedParts, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await AbortMultipartUploadAsync(bucketName, key, upload.UploadId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original upload failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Creates a SigV4 presigned GET URL.
    /// </summary>
    public PresignedRequest CreatePresignedGetUrl(string bucketName, string key, TimeSpan expires)
    {
        return CreatePresignedUrl(HttpMethod.Get, bucketName, key, expires);
    }

    /// <summary>
    /// Creates a SigV4 presigned GET URL.
    /// </summary>
    public Task<PresignedRequest> CreatePresignedGetUrlAsync(
        string bucketName,
        string key,
        TimeSpan expires,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreatePresignedGetUrl(bucketName, key, expires));
    }

    /// <summary>
    /// Creates a SigV4 presigned PUT URL.
    /// </summary>
    public PresignedRequest CreatePresignedPutUrl(string bucketName, string key, TimeSpan expires)
    {
        return CreatePresignedUrl(HttpMethod.Put, bucketName, key, expires);
    }

    /// <summary>
    /// Creates a SigV4 presigned URL for UploadPart.
    /// </summary>
    public PresignedRequest CreatePresignedUploadPartUrl(string bucketName, string key, string uploadId, int partNumber, TimeSpan expires)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        ValidateUploadId(uploadId);
        ValidatePartNumber(partNumber);
        var credentials = _options.Credentials ?? throw new InvalidOperationException("Credentials are required to create presigned URLs.");
        var uri = BuildUri(
            bucketName,
            key,
            new[]
            {
                new KeyValuePair<string, string>("partNumber", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("uploadId", uploadId)
            });
        return SigV4Signer.Presign(uri, HttpMethod.Put, credentials, expires, _options.Region, _options.Service);
    }

    /// <summary>
    /// Creates a SigV4 presigned URL for UploadPart.
    /// </summary>
    public Task<PresignedRequest> CreatePresignedUploadPartUrlAsync(
        string bucketName,
        string key,
        string uploadId,
        int partNumber,
        TimeSpan expires,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreatePresignedUploadPartUrl(bucketName, key, uploadId, partNumber, expires));
    }

    /// <summary>
    /// Creates a SigV4 presigned PUT URL.
    /// </summary>
    public Task<PresignedRequest> CreatePresignedPutUrlAsync(
        string bucketName,
        string key,
        TimeSpan expires,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreatePresignedPutUrl(bucketName, key, expires));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private PresignedRequest CreatePresignedUrl(HttpMethod method, string bucketName, string key, TimeSpan expires)
    {
        ValidateBucketName(bucketName);
        ValidateObjectKey(key);
        var credentials = _options.Credentials ?? throw new InvalidOperationException("Credentials are required to create presigned URLs.");
        var uri = BuildUri(bucketName, key, query: null);
        return SigV4Signer.Presign(uri, method, credentials, expires, _options.Region, _options.Service);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string? bucketName,
        string? key,
        IReadOnlyList<KeyValuePair<string, string>>? query = null)
    {
        var request = new HttpRequestMessage(method, BuildUri(bucketName, key, query));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        if (_options.Credentials is not null)
        {
            SigV4Signer.Sign(request, _options.Credentials, _options.Region, _options.Service);
        }

        return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await MeansError.FromResponseAsync(response).ConfigureAwait(false);
        }
    }

    private Uri BuildUri(string? bucketName, string? key, IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        var endpoint = _options.Endpoint;
        var builder = new UriBuilder(endpoint);
        var endpointPath = endpoint.AbsolutePath == "/" ? "" : endpoint.AbsolutePath.TrimEnd('/');
        var usePathStyle = bucketName is null || _options.ForcePathStyle || IsIpAddressOrLocalhost(endpoint.Host);

        if (bucketName is not null && !usePathStyle)
        {
            builder.Host = bucketName + "." + GetVirtualHostedDomainSuffix(endpoint.Host);
            builder.Path = CombinePath(endpointPath, key is null ? "" : EscapeKey(key));
        }
        else
        {
            var path = bucketName is null
                ? endpointPath
                : CombinePath(endpointPath, EscapePathSegment(bucketName), key is null ? "" : EscapeKey(key));
            builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        }

        if (query is not null && query.Count > 0)
        {
            builder.Query = BuildQuery(query);
        }
        else
        {
            builder.Query = "";
        }

        return builder.Uri;
    }

    private static ObjectHeadResult ReadObjectHeaders(HttpResponseMessage response, string bucketName, string key)
    {
        var result = new ObjectHeadResult
        {
            BucketName = bucketName,
            Key = key,
            StatusCode = response.StatusCode,
            ETag = S3XmlParser.NormalizeETag(HeaderValue(response, "ETag")),
            ContentLength = response.Content.Headers.ContentLength,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            LastModified = response.Content.Headers.LastModified ?? response.Headers.Date,
            CacheControl = response.Headers.CacheControl?.ToString(),
            ContentDisposition = response.Content.Headers.ContentDisposition?.ToString(),
            ContentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault(),
            AcceptRanges = HeaderValue(response, "Accept-Ranges"),
            VersionId = HeaderValue(response, "x-amz-version-id"),
            RequestId = HeaderValue(response, "x-amz-request-id")
        };

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                result.Metadata[header.Key.Substring("x-amz-meta-".Length)] = string.Join(",", header.Value);
            }
        }

        return result;
    }

    private static void AddMetadataHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("Metadata keys cannot be empty.", nameof(metadata));
            }

            var headerName = pair.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? pair.Key
                : "x-amz-meta-" + pair.Key;
            request.Headers.TryAddWithoutValidation(headerName, pair.Value ?? "");
        }
    }

    private static void ValidateBucketName(string bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name is required.", nameof(bucketName));
        }
    }

    private static void ValidateObjectKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Object key is required.", nameof(key));
        }
    }

    private static void ValidateUploadId(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new ArgumentException("Upload id is required.", nameof(uploadId));
        }
    }

    private static void ValidatePartNumber(int partNumber)
    {
        if (partNumber is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber), "Part number must be between 1 and 10000.");
        }
    }

    private static void ValidateCompletedParts(IReadOnlyList<CompletedMultipartPart> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("At least one completed part is required.", nameof(parts));
        }

        var previous = 0;
        foreach (var part in parts)
        {
            ValidatePartNumber(part.PartNumber);
            if (part.PartNumber <= previous)
            {
                throw new ArgumentException("Completed parts must be in ascending part number order.", nameof(parts));
            }

            if (string.IsNullOrWhiteSpace(part.ETag))
            {
                throw new ArgumentException("Completed part ETags are required.", nameof(parts));
            }

            previous = part.PartNumber;
        }
    }

    private static string BuildCompleteMultipartXml(IReadOnlyList<CompletedMultipartPart> parts)
    {
        var builder = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var part in parts)
        {
            builder.Append("<Part><PartNumber>")
                .Append(part.PartNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("</PartNumber><ETag>&quot;")
                .Append(XmlEscape(S3XmlParser.NormalizeETag(part.ETag) ?? part.ETag))
                .Append("&quot;</ETag></Part>");
        }

        builder.Append("</CompleteMultipartUpload>");
        return builder.ToString();
    }

    private static async Task<MemoryStream> ReadPartAsync(Stream content, int partSize, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var buffer = new byte[Math.Min(81920, partSize)];
        var remaining = partSize;
        while (remaining > 0)
        {
            var read = await content.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        output.Position = 0;
        return output;
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static void AddQuery(List<KeyValuePair<string, string>> query, string key, string? value)
    {
        if (value is not null)
        {
            query.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> query)
    {
        return string.Join("&",
            query.Select(pair => new KeyValuePair<string, string>(EscapeQueryComponent(pair.Key), EscapeQueryComponent(pair.Value)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string CombinePath(params string[] parts)
    {
        return "/" + string.Join("/", parts
            .Where(part => !string.IsNullOrEmpty(part))
            .Select(part => part.Trim('/')));
    }

    private static string EscapeKey(string key)
    {
        return string.Join("/", key.Split('/').Select(EscapePathSegment));
    }

    private static string EscapePathSegment(string value)
    {
        return Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
    }

    private static string EscapeQueryComponent(string value)
    {
        return Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;
    }

    private static bool IsIpAddressOrLocalhost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out _);
    }

    private string GetVirtualHostedDomainSuffix(string endpointHost)
    {
        if (!string.IsNullOrWhiteSpace(_options.VirtualHostedDomainSuffix))
        {
            return _options.VirtualHostedDomainSuffix!;
        }

        return endpointHost.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
            ? endpointHost.Substring("api.".Length)
            : endpointHost;
    }
}
