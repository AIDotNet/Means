namespace Means.Infrastructure.SqliteFs;

/// <summary>
/// Runtime options for the single-node SQLite + filesystem storage adapter.
/// These settings deliberately describe only local durability concerns; future
/// server-pool or erasure-set options should live in a separate cluster layer.
/// </summary>
public sealed class SqliteFsOptions
{
    /// <summary>
    /// SQLite database file used for bucket/object metadata, policies, and access keys.
    /// Relative paths are resolved under the application base directory during store construction.
    /// </summary>
    public string DatabasePath { get; set; } = "data/means.db";

    /// <summary>
    /// Directory containing opaque object blob files.
    /// Object keys are never used as file names; every object version gets a generated object id.
    /// </summary>
    public string ObjectsPath { get; set; } = "data/objects";

    /// <summary>
    /// Development bootstrap access key inserted on first database initialization.
    /// This is intentionally simple for the current single-node baseline.
    /// </summary>
    public string DefaultAccessKey { get; set; } = "meansadmin";

    /// <summary>
    /// Development bootstrap secret key paired with <see cref="DefaultAccessKey"/>.
    /// Production deployments should replace this through configuration.
    /// </summary>
    public string DefaultSecretKey { get; set; } = "meansadminsecret";

    /// <summary>
    /// Age after which incomplete multipart uploads are considered abandoned and can be cleaned up.
    /// </summary>
    public int MultipartUploadCleanupAgeHours { get; set; } = 24;

    /// <summary>
    /// Background cleanup cadence for abandoned multipart uploads.
    /// </summary>
    public int MultipartUploadCleanupIntervalMinutes { get; set; } = 60;
}
