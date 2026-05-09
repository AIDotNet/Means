namespace Means.Infrastructure.XlFs;

public sealed class XlFsOptions
{
    public const string BackendName = "XlFs";

    public string Backend { get; set; } = "SqliteFs";

    public string DatabasePath { get; set; } = "data/means.db";

    public string ObjectsPath { get; set; } = "data/objects";

    public string[] Disks { get; set; } = [];

    public string DeploymentId { get; set; } = "";

    public string SetId { get; set; } = "set-1";

    public int ErasureDataShards { get; set; } = 1;

    public int ErasureParityShards { get; set; }

    public int WriteQuorum { get; set; }

    public int ReadQuorum { get; set; }

    public string MetaSyncMode { get; set; } = XlMetaSyncModes.Always;

    public bool AllowNewFormatWithExistingSqlite { get; set; }

    public bool VerifyChecksumOnRead { get; set; }

    public string DefaultAccessKey { get; set; } = "meansadmin";

    public string DefaultSecretKey { get; set; } = "meansadminsecret";

    public int ScannerBatchSize { get; set; } = 1000;

    public int HealBatchSize { get; set; } = 100;

    public int GarbageCollectionBatchSize { get; set; } = 1000;

    public int GarbageCollectionTempFileAgeMinutes { get; set; } = 60;

    public int MultipartUploadCleanupAgeHours { get; set; } = 24;

    public int ReplicaRepairMaxAttempts { get; set; } = 5;
}

public static class XlMetaSyncModes
{
    public const string Always = "Always";
    public const string Batch = "Batch";
    public const string None = "None";
}
