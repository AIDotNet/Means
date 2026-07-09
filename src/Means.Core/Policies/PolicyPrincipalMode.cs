namespace Means.Core;

/// <summary>
/// Controls how policy Principal matching behaves for bucket vs access-key policies.
/// </summary>
public enum PolicyPrincipalMode
{
    /// <summary>
    /// Bucket policy mode: Principal is required and must match "*" or the caller principal.
    /// </summary>
    Bucket,

    /// <summary>
    /// Access-key policy mode: missing Principal matches; when present it must match "*" or the access key.
    /// </summary>
    AccessKey
}
