namespace Means.Core;

/// <summary>
/// Public access-key metadata safe to return to the console UI.
/// Secret keys are intentionally excluded and are only returned once at creation time.
/// </summary>
public sealed record AccessKeyInfo(string AccessKey, bool Enabled, DateTimeOffset CreatedAt);
