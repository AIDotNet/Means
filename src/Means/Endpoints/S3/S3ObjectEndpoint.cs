using Means.Core;
using Means.Protocol.S3;
using Microsoft.Net.Http.Headers;

namespace Means.Endpoints.S3;

/// <summary>
/// Handles object-key operations.
/// This layer translates HTTP headers/body into storage commands and leaves streaming details to the response writer.
/// </summary>
internal static class S3ObjectEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        IConsoleStore consoleStore,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var method = context.Request.Method;
        if (context.Request.Query.ContainsKey("tagging"))
        {
            await HandleObjectTaggingAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPost(method) && context.Request.Query.ContainsKey("uploads"))
        {
            await InitiateMultipartUploadAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(method) && context.Request.Query.ContainsKey("uploadId") && context.Request.Headers.ContainsKey("x-amz-copy-source"))
        {
            await UploadPartCopyAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(method) && context.Request.Query.ContainsKey("uploadId"))
        {
            await UploadPartAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPost(method) && context.Request.Query.ContainsKey("uploadId"))
        {
            await CompleteMultipartUploadAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query.ContainsKey("uploadId"))
        {
            await ListPartsAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsDelete(method) && context.Request.Query.ContainsKey("uploadId"))
        {
            await AbortMultipartUploadAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(method) && context.Request.Headers.ContainsKey("x-amz-copy-source"))
        {
            await CopyObjectAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            await EnforceWritePreconditionsAsync(context, bucketName, key, store, cancellationToken);
            await PutObjectAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetObject, bucketName, key, requireAuthenticated: false, cancellationToken);
            var bucketSettings = await consoleStore.GetBucketSettingsAsync(bucketName, cancellationToken);
            await S3ResponseWriter.WriteObjectAsync(
                context,
                store,
                bucketName,
                key,
                context.Request.Query["versionId"].FirstOrDefault(),
                bucketSettings,
                headOnly: HttpMethods.IsHead(method),
                cancellationToken);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.DeleteObject, bucketName, key, requireAuthenticated: false, cancellationToken);
            var result = await store.DeleteObjectAsync(bucketName, key, context.Request.Query["versionId"].FirstOrDefault(), cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            if (!string.IsNullOrWhiteSpace(result.VersionId))
            {
                context.Response.Headers["x-amz-version-id"] = result.VersionId;
            }

            if (result.DeleteMarker)
            {
                context.Response.Headers["x-amz-delete-marker"] = "true";
            }

            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported object operation.", 400);
    }

    private static async Task InitiateMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var upload = await store.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest(
                bucketName,
                key,
                context.Request.ContentType ?? "application/octet-stream",
                S3RequestParser.ExtractMetadata(context),
                S3RequestParser.GetHeader(context, HeaderNames.CacheControl),
                S3RequestParser.GetHeader(context, HeaderNames.ContentDisposition)),
            cancellationToken);

        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.InitiateMultipartUploadResult(upload), cancellationToken);
    }

    private static async Task UploadPartAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var uploadId = S3RequestParser.ParseUploadId(context.Request.Query["uploadId"].FirstOrDefault());
        var partNumber = S3RequestParser.ParsePartNumber(context.Request.Query["partNumber"].FirstOrDefault());
        var part = await store.UploadPartAsync(new UploadPartRequest(bucketName, key, uploadId, partNumber, context.Request.Body), cancellationToken);

        context.Items[S3MetricsItems.IngressBytes] = part.Size;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ETag = S3Xml.QuoteEtag(part.ETag);
    }

    private static async Task UploadPartCopyAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var uploadId = S3RequestParser.ParseUploadId(context.Request.Query["uploadId"].FirstOrDefault());
        var partNumber = S3RequestParser.ParsePartNumber(context.Request.Query["partNumber"].FirstOrDefault());
        var (sourceBucket, sourceKey, sourceVersionId) = S3RequestParser.ParseCopySource(context.Request.Headers["x-amz-copy-source"].ToString());
        await authorizer.AuthorizeAsync(context, S3Actions.GetObject, sourceBucket, sourceKey, requireAuthenticated: false, cancellationToken);

        await using var source = await store.GetObjectAsync(sourceBucket, sourceKey, sourceVersionId, cancellationToken);
        var range = S3RequestParser.ParseCopySourceRange(S3RequestParser.GetHeader(context, "x-amz-copy-source-range"), source.Info.ContentLength);
        Stream content = source.Content;
        if (range is not null)
        {
            content = new BoundedReadStream(source.Content, range.Value.Start, range.Value.End - range.Value.Start + 1);
        }

        await using (content == source.Content ? null : content)
        {
            var part = await store.UploadPartAsync(new UploadPartRequest(bucketName, key, uploadId, partNumber, content), cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.CopyPartResult(part), cancellationToken);
        }
    }

    private static async Task CompleteMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var uploadId = S3RequestParser.ParseUploadId(context.Request.Query["uploadId"].FirstOrDefault());
        var parts = await S3RequestParser.ParseCompleteMultipartUploadAsync(context.Request.Body, cancellationToken);
        var completed = await store.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest(
                bucketName,
                key,
                uploadId,
                parts,
                S3RequestParser.GetHeader(context, "x-means-idempotency-key")),
            cancellationToken);

        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.CompleteMultipartUploadResult(completed), cancellationToken);
    }

    private static async Task ListPartsAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.ListMultipartUploadParts, bucketName, key, requireAuthenticated: false, cancellationToken);
        var uploadId = S3RequestParser.ParseUploadId(context.Request.Query["uploadId"].FirstOrDefault());
        var partNumberMarker = int.TryParse(context.Request.Query["part-number-marker"].FirstOrDefault(), out var parsedMarker) ? parsedMarker : 0;
        var maxParts = S3RequestParser.ParseMaxParts(context.Request.Query["max-parts"].FirstOrDefault());
        var result = await store.ListPartsAsync(bucketName, key, uploadId, partNumberMarker, maxParts, cancellationToken);
        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListParts(result), cancellationToken);
    }

    private static async Task AbortMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.AbortMultipartUpload, bucketName, key, requireAuthenticated: false, cancellationToken);
        var uploadId = S3RequestParser.ParseUploadId(context.Request.Query["uploadId"].FirstOrDefault());
        await store.AbortMultipartUploadAsync(bucketName, key, uploadId, cancellationToken);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task PutObjectAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                bucketName,
                key,
                context.Request.Body,
                context.Request.ContentType ?? "application/octet-stream",
                S3RequestParser.ExtractMetadata(context),
                S3RequestParser.GetHeader(context, HeaderNames.CacheControl),
                S3RequestParser.GetHeader(context, HeaderNames.ContentDisposition),
                S3RequestParser.GetHeader(context, "x-means-idempotency-key")),
            cancellationToken);

        context.Items[S3MetricsItems.IngressBytes] = info.ContentLength;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ETag = S3Xml.QuoteEtag(info.ETag);
        context.Response.Headers["x-amz-version-id"] = info.ObjectId;
    }

    private static async Task CopyObjectAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(context, S3Actions.PutObject, bucketName, key, requireAuthenticated: false, cancellationToken);
        var (sourceBucket, sourceKey, sourceVersionId) = S3RequestParser.ParseCopySource(context.Request.Headers["x-amz-copy-source"].ToString());
        await authorizer.AuthorizeAsync(context, S3Actions.GetObject, sourceBucket, sourceKey, requireAuthenticated: false, cancellationToken);

        var copied = await store.CopyObjectAsync(
            new CopyObjectRequest(
                sourceBucket,
                sourceKey,
                sourceVersionId,
                bucketName,
                key,
                S3RequestParser.ExtractMetadata(context),
                S3RequestParser.ParseMetadataDirective(context),
                context.Request.ContentType,
                S3RequestParser.GetHeader(context, HeaderNames.CacheControl),
                S3RequestParser.GetHeader(context, HeaderNames.ContentDisposition)),
            cancellationToken);

        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.CopyObjectResult(copied), cancellationToken);
    }

    private static async Task HandleObjectTaggingAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var versionId = context.Request.Query["versionId"].FirstOrDefault();
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetObjectTagging, bucketName, key, requireAuthenticated: false, cancellationToken);
            var tags = await store.GetObjectTaggingAsync(bucketName, key, versionId, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ObjectTagging(tags), cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutObjectTagging, bucketName, key, requireAuthenticated: false, cancellationToken);
            var tags = await S3RequestParser.ParseTaggingAsync(context.Request.Body, cancellationToken);
            await store.PutObjectTaggingAsync(bucketName, key, versionId, tags, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.DeleteObjectTagging, bucketName, key, requireAuthenticated: false, cancellationToken);
            await store.DeleteObjectTaggingAsync(bucketName, key, versionId, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported object tagging operation.", 400);
    }

    private static async Task EnforceWritePreconditionsAsync(
        HttpContext context,
        string bucketName,
        string key,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        var ifMatch = S3RequestParser.GetHeader(context, HeaderNames.IfMatch);
        var ifNoneMatch = S3RequestParser.GetHeader(context, HeaderNames.IfNoneMatch);
        if (ifMatch is null && ifNoneMatch is null)
        {
            return;
        }

        ObjectInfo? current = null;
        try
        {
            current = await store.HeadObjectAsync(bucketName, key, cancellationToken);
        }
        catch (MeansException ex) when (ex.Code == MeansErrorCodes.NoSuchKey)
        {
        }

        if (ifMatch is not null && (current is null || !EtagConditionMatches(ifMatch, current.ETag)))
        {
            throw new MeansException(MeansErrorCodes.PreconditionFailed, "At least one precondition failed.", StatusCodes.Status412PreconditionFailed);
        }

        if (ifNoneMatch is not null && current is not null && EtagConditionMatches(ifNoneMatch, current.ETag))
        {
            throw new MeansException(MeansErrorCodes.PreconditionFailed, "At least one precondition failed.", StatusCodes.Status412PreconditionFailed);
        }
    }

    private static bool EtagConditionMatches(string condition, string etag)
    {
        if (condition.Trim() == "*")
        {
            return true;
        }

        return condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate.Trim('"'), etag.Trim('"'), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _inner.Seek(start, SeekOrigin.Begin);
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
