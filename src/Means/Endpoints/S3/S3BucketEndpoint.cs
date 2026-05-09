using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Handles bucket-scoped S3 operations such as create, head, delete, and ListObjectsV2.
/// Object-key operations are handled separately so bucket behavior remains easy to scan.
/// </summary>
internal static class S3BucketEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsPut(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.CreateBucket, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.CreateBucketAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.Location = "/" + bucketName;
            return;
        }

        if (HttpMethods.IsHead(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
            var bucket = await store.GetBucketAsync(bucketName, cancellationToken);
            if (bucket is null)
            {
                throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.DeleteBucket, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query.ContainsKey("uploads"))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
            var options = new ListMultipartUploadsOptions(
                context.Request.Query["prefix"].FirstOrDefault(),
                context.Request.Query["key-marker"].FirstOrDefault(),
                context.Request.Query["upload-id-marker"].FirstOrDefault(),
                S3RequestParser.ParseMaxUploads(context.Request.Query["max-uploads"].FirstOrDefault()));
            var result = await store.ListMultipartUploadsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListMultipartUploads(result), cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query["list-type"] == "2")
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucket, bucketName, null, requireAuthenticated: false, cancellationToken);
            var options = new ListObjectsOptions(
                context.Request.Query["prefix"].FirstOrDefault(),
                context.Request.Query["delimiter"].FirstOrDefault(),
                context.Request.Query["continuation-token"].FirstOrDefault(),
                S3RequestParser.ParseMaxKeys(context.Request.Query["max-keys"].FirstOrDefault()));
            var result = await store.ListObjectsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListObjectsV2(result), cancellationToken);
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported bucket operation.", 400);
    }
}
