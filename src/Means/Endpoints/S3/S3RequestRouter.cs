using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Routes a resolved S3 address into service-level, bucket-level, or object-level handlers.
/// Keeping this logic separate from endpoint execution makes later API expansion straightforward.
/// </summary>
internal static class S3RequestRouter
{
    public static async Task DispatchAsync(
        HttpContext context,
        S3Address address,
        IObjectStore store,
        IConsoleStore consoleStore,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (address.BucketName is null)
        {
            await DispatchServiceAsync(context, store, authorizer, cancellationToken);
            return;
        }

        S3RequestParser.ValidateBucketName(address.BucketName);
        if (address.ObjectKey is null)
        {
            await S3BucketEndpoint.HandleAsync(context, address.BucketName, store, authorizer, cancellationToken);
            return;
        }

        S3RequestParser.ValidateObjectKey(address.ObjectKey);
        await S3ObjectEndpoint.HandleAsync(context, address.BucketName, address.ObjectKey, store, consoleStore, authorizer, cancellationToken);
    }

    private static async Task DispatchServiceAsync(
        HttpContext context,
        IObjectStore store,
        S3RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Unsupported service-level operation.", 400);
        }

        await authorizer.AuthorizeAsync(context, S3Actions.ListAllMyBuckets, null, null, requireAuthenticated: true, cancellationToken);
        var buckets = await store.ListBucketsAsync(cancellationToken);
        await S3ResponseWriter.WriteXmlAsync(context, StatusCodes.Status200OK, S3Xml.ListBuckets(buckets), cancellationToken);
    }
}
