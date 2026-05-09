using Means.Core;
using Means.Protocol.S3;

namespace Means.Endpoints.S3;

/// <summary>
/// Coordinates SigV4 authentication and bucket policy authorization for one S3 request.
/// This class is deliberately HTTP-aware, while policy parsing and credential storage remain in lower layers.
/// </summary>
internal sealed class S3RequestAuthorizer(
    IAccessKeyStore accessKeys,
    IBucketPolicyRepository policies,
    BucketPolicyEvaluator policyEvaluator,
    SigV4RequestVerifier verifier)
{
    public async Task AuthorizeAsync(
        HttpContext context,
        string action,
        string? bucketName,
        string? key,
        bool requireAuthenticated,
        CancellationToken cancellationToken)
    {
        var auth = await verifier.VerifyAsync(context.Request, accessKeys.GetCredentialAsync, cancellationToken);
        if (auth.IsSigned && !auth.IsAuthenticated)
        {
            throw new MeansException(auth.ErrorCode ?? MeansErrorCodes.AccessDenied, auth.ErrorMessage ?? "Access denied.", 403);
        }

        if (bucketName is null)
        {
            if (!auth.IsAuthenticated)
            {
                throw new MeansException(MeansErrorCodes.AccessDenied, "Authentication is required.", 403);
            }

            return;
        }

        var policy = await policies.GetPolicyAsync(bucketName, cancellationToken);
        var decision = policyEvaluator.Evaluate(policy, action, bucketName, key, auth.AccessKey);
        if (decision == PolicyDecision.Deny)
        {
            throw new MeansException(MeansErrorCodes.AccessDenied, "Access denied by bucket policy.", 403);
        }

        if (!auth.IsAuthenticated && (requireAuthenticated || decision != PolicyDecision.Allow))
        {
            throw new MeansException(MeansErrorCodes.AccessDenied, "Authentication is required.", 403);
        }
    }

    public async Task RequireAuthenticatedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var auth = await verifier.VerifyAsync(context.Request, accessKeys.GetCredentialAsync, cancellationToken);
        if (!auth.IsAuthenticated)
        {
            throw new MeansException(auth.ErrorCode ?? MeansErrorCodes.AccessDenied, auth.ErrorMessage ?? "Authentication is required.", 403);
        }
    }
}
