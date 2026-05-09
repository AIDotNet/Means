namespace Means.Core;

/// <summary>
/// Result of evaluating a bucket policy against one request.
/// Neutral means the policy did not match, so authentication requirements still apply.
/// </summary>
public enum PolicyDecision
{
    Neutral,
    Allow,
    Deny
}
