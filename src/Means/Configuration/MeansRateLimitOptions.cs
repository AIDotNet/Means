namespace Means.Configuration;

public sealed class MeansRateLimitOptions
{
    public bool Enabled { get; set; } = true;

    public int ConsoleLoginPermitLimit { get; set; } = 10;

    public int ConsoleLoginWindowSeconds { get; set; } = 60;

    public int ConsoleApiPermitLimit { get; set; } = 600;

    public int ConsoleApiWindowSeconds { get; set; } = 60;

    public int S3PermitLimit { get; set; } = 1200;

    public int S3WindowSeconds { get; set; } = 60;
}
