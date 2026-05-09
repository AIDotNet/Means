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
        if (HttpMethods.IsPost(method) && context.Request.Query.ContainsKey("uploads"))
        {
            await InitiateMultipartUploadAsync(context, bucketName, key, store, authorizer, cancellationToken);
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
            await PutObjectAsync(context, bucketName, key, store, authorizer, cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetObject, bucketName, key, requireAuthenticated: false, cancellationToken);
            var bucketSettings = await consoleStore.GetBucketSettingsAsync(bucketName, cancellationToken);
            await S3ResponseWriter.WriteObjectAsync(context, store, bucketName, key, bucketSettings, headOnly: HttpMethods.IsHead(method), cancellationToken);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.DeleteObject, bucketName, key, requireAuthenticated: false, cancellationToken);
            await store.DeleteObjectAsync(bucketName, key, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
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
        var completed = await store.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest(bucketName, key, uploadId, parts), cancellationToken);

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
        var result = await store.ListPartsAsync(bucketName, key, uploadId, cancellationToken);
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
                S3RequestParser.GetHeader(context, HeaderNames.ContentDisposition)),
            cancellationToken);

        context.Items[S3MetricsItems.IngressBytes] = info.ContentLength;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ETag = S3Xml.QuoteEtag(info.ETag);
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
        var (sourceBucket, sourceKey) = S3RequestParser.ParseCopySource(context.Request.Headers["x-amz-copy-source"].ToString());
        await authorizer.AuthorizeAsync(context, S3Actions.GetObject, sourceBucket, sourceKey, requireAuthenticated: false, cancellationToken);

        var copied = await store.CopyObjectAsync(
            new CopyObjectRequest(
                sourceBucket,
                sourceKey,
                bucketName,
                key,
                S3RequestParser.ExtractMetadata(context),
                S3RequestParser.GetHeader(context, HeaderNames.CacheControl),
                S3RequestParser.GetHeader(context, HeaderNames.ContentDisposition)),
            cancellationToken);

        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.CopyObjectResult(copied), cancellationToken);
    }
}
