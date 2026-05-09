using System.Text;
using System.Text.Json;
using Means.Core;

namespace Means.Endpoints.S3;

/// <summary>
/// Handles the bucket policy management subresource: GET/PUT/DELETE ?policy.
/// These endpoints require a signed request because policy mutation controls anonymous access.
/// </summary>
internal static class S3PolicyEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        string bucketName,
        IBucketPolicyRepository policies,
        S3RequestAuthorizer authorizer,
        string requestId,
        CancellationToken cancellationToken)
    {
        await authorizer.RequireAuthenticatedAsync(context, cancellationToken);

        if (HttpMethods.IsGet(context.Request.Method))
        {
            var policy = await policies.GetPolicyAsync(bucketName, cancellationToken);
            if (policy is null)
            {
                throw new MeansException("NoSuchBucketPolicy", "Bucket policy does not exist.", 404);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(policy, cancellationToken);
            return;
        }

        if (HttpMethods.IsPut(context.Request.Method))
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);
            using var _ = JsonDocument.Parse(json);
            await policies.PutPolicyAsync(bucketName, json, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            await policies.DeletePolicyAsync(bucketName, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await S3ResponseWriter.WriteErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            MeansErrorCodes.InvalidRequest,
            "Unsupported policy operation.",
            requestId,
            responseHeaders: null,
            cancellationToken);
    }
}
