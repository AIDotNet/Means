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
        if (context.Request.Query.ContainsKey("versioning"))
        {
            await HandleVersioningAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("lifecycle"))
        {
            await HandleLifecycleAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("cors"))
        {
            await HandleCorsAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

        if (context.Request.Query.ContainsKey("notification"))
        {
            await HandleNotificationAsync(context, bucketName, store, authorizer, cancellationToken);
            return;
        }

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
                context.Request.Query["delimiter"].FirstOrDefault(),
                context.Request.Query["key-marker"].FirstOrDefault(),
                context.Request.Query["upload-id-marker"].FirstOrDefault(),
                S3RequestParser.ParseMaxUploads(context.Request.Query["max-uploads"].FirstOrDefault()));
            var result = await store.ListMultipartUploadsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListMultipartUploads(result), cancellationToken);
            return;
        }

        if (HttpMethods.IsGet(method) && context.Request.Query.ContainsKey("versions"))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.ListBucketVersions, bucketName, null, requireAuthenticated: false, cancellationToken);
            var options = new ListObjectVersionsOptions(
                context.Request.Query["prefix"].FirstOrDefault(),
                context.Request.Query["delimiter"].FirstOrDefault(),
                context.Request.Query["key-marker"].FirstOrDefault(),
                context.Request.Query["version-id-marker"].FirstOrDefault(),
                S3RequestParser.ParseMaxKeys(context.Request.Query["max-keys"].FirstOrDefault()));
            var result = await store.ListObjectVersionsAsync(bucketName, options, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListObjectVersions(result), cancellationToken);
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

    private static async Task HandleVersioningAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketVersioning, bucketName, null, requireAuthenticated: false, cancellationToken);
            var versioning = await store.GetBucketVersioningAsync(bucketName, cancellationToken);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.BucketVersioning(versioning), cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketVersioning, bucketName, null, requireAuthenticated: true, cancellationToken);
            var status = await S3RequestParser.ParseBucketVersioningStatusAsync(context.Request.Body, cancellationToken);
            await store.PutBucketVersioningAsync(bucketName, status, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported versioning operation.", 400);
    }

    private static async Task HandleLifecycleAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetLifecycleConfiguration, bucketName, null, requireAuthenticated: false, cancellationToken);
            var lifecycle = await store.GetBucketLifecycleAsync(bucketName, cancellationToken)
                ?? throw new MeansException(MeansErrorCodes.NoSuchLifecycleConfiguration, "Lifecycle configuration does not exist.", 404);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.BucketLifecycle(lifecycle), cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutLifecycleConfiguration, bucketName, null, requireAuthenticated: true, cancellationToken);
            var lifecycle = await S3RequestParser.ParseLifecycleAsync(context.Request.Body, cancellationToken);
            await store.PutBucketLifecycleAsync(bucketName, lifecycle, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutLifecycleConfiguration, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketLifecycleAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported lifecycle operation.", 400);
    }

    private static async Task HandleCorsAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketCORS, bucketName, null, requireAuthenticated: false, cancellationToken);
            var cors = await store.GetBucketCorsAsync(bucketName, cancellationToken)
                ?? throw new MeansException(MeansErrorCodes.NoSuchCORSConfiguration, "CORS configuration does not exist.", 404);
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, cors.Xml, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketCORS, bucketName, null, requireAuthenticated: true, cancellationToken);
            var xml = await S3RequestParser.ReadAndValidateXmlAsync(context.Request.Body, "CORSConfiguration", cancellationToken);
            await store.PutBucketCorsAsync(bucketName, new BucketCorsConfiguration(xml), cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketCORS, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketCorsAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported CORS operation.", 400);
    }

    private static async Task HandleNotificationAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.GetBucketNotification, bucketName, null, requireAuthenticated: false, cancellationToken);
            var notification = await store.GetBucketNotificationAsync(bucketName, cancellationToken)
                ?? new BucketNotificationConfiguration("<NotificationConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\" />");
            await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, notification.Xml, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketNotification, bucketName, null, requireAuthenticated: true, cancellationToken);
            var xml = await S3RequestParser.ReadAndValidateXmlAsync(context.Request.Body, "NotificationConfiguration", cancellationToken);
            await store.PutBucketNotificationAsync(bucketName, new BucketNotificationConfiguration(xml), cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await authorizer.AuthorizeAsync(context, S3Actions.PutBucketNotification, bucketName, null, requireAuthenticated: true, cancellationToken);
            await store.DeleteBucketNotificationAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported notification operation.", 400);
    }
}
