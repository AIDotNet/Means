namespace Means.Core;

/// <summary>
/// Credential lookup boundary used by SigV4 verification.
/// The protocol layer only needs this small read interface, not the full storage implementation.
/// </summary>
public interface IAccessKeyStore
{
    Task<AccessKeyCredential?> GetCredentialAsync(string accessKey, CancellationToken cancellationToken);
}
