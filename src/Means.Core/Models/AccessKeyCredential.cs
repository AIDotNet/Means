namespace Means.Core;

/// <summary>
/// Access-key credential used by SigV4 authentication.
/// SecretKey is stored here because this first implementation verifies signatures locally.
/// </summary>
public sealed record AccessKeyCredential(string AccessKey, string SecretKey, bool Enabled);
