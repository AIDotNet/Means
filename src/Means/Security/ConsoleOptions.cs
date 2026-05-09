namespace Means.Security;

/// <summary>
/// Local administrator configuration for the built-in console.
/// Development has deterministic defaults; production must override both values at startup.
/// </summary>
public sealed class ConsoleOptions
{
    public const string DefaultAdminUser = "admin";
    public const string DefaultAdminPassword = "meansadmin";

    public string AdminUser { get; set; } = DefaultAdminUser;

    public string AdminPassword { get; set; } = DefaultAdminPassword;

    public int SessionHours { get; set; } = 8;
}
