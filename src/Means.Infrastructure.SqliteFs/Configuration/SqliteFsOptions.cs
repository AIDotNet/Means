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
    /// Number of object replicas to write through the current SQLite/filesystem adapter.
    /// Values greater than 1 require enough online disks in the cluster topology.
    /// </summary>
    public int ReplicaCount { get; set; } = 1;

    /// <summary>
    /// Heartbeat age after which a node or disk is not eligible for replica placement.
    /// </summary>
    public int ReplicaOfflineAfterSeconds { get; set; } = 60;

    /// <summary>
    /// Background cadence for scanning replica manifests and repairing missing files.
    /// </summary>
    public int ReplicaRepairIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of queued replica repair items processed per background pass.
    /// </summary>
    public int ReplicaRepairBatchSize { get; set; } = 100;

    /// <summary>
    /// Failed repair items remain queued until this attempt count is reached.
    /// </summary>
    public int ReplicaRepairMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Background cadence for migrating replicas away from offline disks or stale placements.
    /// </summary>
    public int RebalanceIntervalSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum number of replica/shard migration items processed per rebalance pass.
    /// </summary>
    public int RebalanceBatchSize { get; set; } = 100;

    /// <summary>
    /// Background cadence for rebuilding missing erasure-coded shards.
    /// </summary>
    public int ErasureCodingRepairIntervalSeconds { get; set; } = 600;

    /// <summary>
    /// Background cadence for S3 lifecycle expiration and multipart-abort rules.
    /// </summary>
    public int LifecycleIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of lifecycle actions applied per background pass.
    /// </summary>
    public int LifecycleBatchSize { get; set; } = 100;

    /// <summary>
    /// Background cadence for verifying persisted replica checksums.
    /// </summary>
    public int ScrubIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of replica files checksummed per scrub pass.
    /// </summary>
    public int ScrubBatchSize { get; set; } = 100;

    /// <summary>
    /// Background cadence for auditing current object metadata against versions and replica manifests.
    /// </summary>
    public int MetadataConsistencyIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of current object rows checked per consistency pass.
    /// </summary>
    public int MetadataConsistencyBatchSize { get; set; } = 1000;

    /// <summary>
    /// Background cadence for deleting unreferenced blob files left behind by interrupted writes.
    /// </summary>
    public int GarbageCollectionIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of candidate files inspected/deleted per garbage collection pass.
    /// </summary>
    public int GarbageCollectionBatchSize { get; set; } = 1000;

    /// <summary>
    /// Unreferenced blob files and temporary files younger than this age are skipped so active writes are never collected.
    /// </summary>
    public int GarbageCollectionTempFileAgeMinutes { get; set; } = 60;

    /// <summary>
    /// Background cadence reserved for replication workers once replication rules are configured.
    /// </summary>
    public int ReplicationIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Total in-memory budget for caching repeatedly-read small objects. Set to 0 to disable.
    /// </summary>
    public long HotObjectCacheMaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Largest individual object eligible for the hot object cache.
    /// </summary>
    public long HotObjectCacheMaxObjectBytes { get; set; } = 1L * 1024 * 1024;

    /// <summary>
    /// When enabled, replica SHA-256 checksums are verified before a replica is selected for reads.
    /// This is disabled by default because it adds full-object hashing to the read latency path.
    /// </summary>
    public bool VerifyChecksumOnRead { get; set; }

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
