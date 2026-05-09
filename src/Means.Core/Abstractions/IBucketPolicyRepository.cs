namespace Means.Core;

/// <summary>
/// Persists bucket policy documents separately from policy evaluation.
/// Policy text is stored as JSON so later versions can expand AWS-compatible syntax without schema churn.
/// </summary>
public interface IBucketPolicyRepository
{
    Task<string?> GetPolicyAsync(string bucketName, CancellationToken cancellationToken);

    Task PutPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken);

    Task DeletePolicyAsync(string bucketName, CancellationToken cancellationToken);
}
