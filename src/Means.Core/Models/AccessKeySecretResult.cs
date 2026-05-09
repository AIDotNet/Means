namespace Means.Core;

/// <summary>
/// Access-key creation result. The SecretKey value is write-only from the UI perspective
/// and must not be persisted in browser storage.
/// </summary>
public sealed record AccessKeySecretResult(string AccessKey, string SecretKey, bool Enabled, DateTimeOffset CreatedAt);
