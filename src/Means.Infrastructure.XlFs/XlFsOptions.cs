namespace Means.Infrastructure.XlFs;

public sealed class XlFsOptions
{
    public string ObjectsPath { get; set; } = "data/objects";

    public string[] Disks { get; set; } = [];

    public string DeploymentId { get; set; } = "";

    public string SetId { get; set; } = "set-1";

    public int ErasureDataShards { get; set; } = 1;

    public int ErasureParityShards { get; set; }

    public int WriteQuorum { get; set; }

    public int ReadQuorum { get; set; }

    public int ReplicaCount { get; set; } = 1;

    public int ReplicaOfflineAfterSeconds { get; set; } = 60;

    public string MetaSyncMode { get; set; } = XlMetaSyncModes.Always;

    public bool VerifyChecksumOnRead { get; set; }

    public string DefaultAccessKey { get; set; } = "meansadmin";

    public string DefaultSecretKey { get; set; } = "meansadminsecret";

    public int ScannerBatchSize { get; set; } = 1000;

    public int HealBatchSize { get; set; } = 100;

    public int ReplicaRepairIntervalSeconds { get; set; } = 300;

    public int ReplicaRepairBatchSize { get; set; } = 100;

    public int ReplicaRepairMaxConcurrency { get; set; } = 2;

    public int ReplicaRepairThrottleDelayMilliseconds { get; set; }

    public int ShardTransferMaxConcurrency { get; set; } = 8;

    public long DiskMinAvailableBytesAfterWrite { get; set; } = 1L * 1024 * 1024 * 1024;

    public double DiskMinAvailablePercentAfterWrite { get; set; }

    public int PlacementMinFaultDomains { get; set; }

    public int RebalanceIntervalSeconds { get; set; } = 600;

    public int RebalanceBatchSize { get; set; } = 100;

    public int ErasureCodingRepairIntervalSeconds { get; set; } = 600;

    public int LifecycleIntervalSeconds { get; set; } = 3600;

    public int LifecycleBatchSize { get; set; } = 100;

    public int ScrubIntervalSeconds { get; set; } = 3600;

    public int ScrubBatchSize { get; set; } = 100;

    public int MetadataConsistencyIntervalSeconds { get; set; } = 3600;

    public int MetadataConsistencyBatchSize { get; set; } = 1000;

    public int GarbageCollectionIntervalSeconds { get; set; } = 3600;

    public int GarbageCollectionBatchSize { get; set; } = 1000;

    public int GarbageCollectionTempFileAgeMinutes { get; set; } = 60;

    public int MultipartUploadCleanupAgeHours { get; set; } = 24;

    public int MultipartUploadCleanupIntervalMinutes { get; set; } = 60;

    public int ReplicationIntervalSeconds { get; set; } = 3600;

    public long HotObjectCacheMaxBytes { get; set; } = 64L * 1024 * 1024;

    public long HotObjectCacheMaxObjectBytes { get; set; } = 1L * 1024 * 1024;

    public int ReplicaRepairMaxAttempts { get; set; } = 5;
}

public static class XlMetaSyncModes
{
    public const string Always = "Always";
    public const string Batch = "Batch";
    public const string None = "None";
}

public readonly record struct StoragePathCapacity(long TotalBytes, long AvailableBytes, string CapacityGroupId);

public static class StoragePathCapacityReader
{
    public static StoragePathCapacity Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var drivePath = OperatingSystem.IsWindows() ? Path.GetPathRoot(fullPath) ?? fullPath : fullPath;
        var drive = new DriveInfo(drivePath);
        return new StoragePathCapacity(
            Math.Max(0, drive.TotalSize),
            Math.Max(0, drive.AvailableFreeSpace),
            ResolveCapacityGroupId(fullPath));
    }

    private static string ResolveCapacityGroupId(string fullPath)
    {
        if (OperatingSystem.IsLinux())
        {
            var deviceId = TryResolveLinuxDeviceId(fullPath);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return "linux-device:" + deviceId;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows-volume:" + (Path.GetPathRoot(fullPath) ?? fullPath);
        }

        return "path:" + fullPath;
    }

    private static string? TryResolveLinuxDeviceId(string fullPath)
    {
        const string mountInfoPath = "/proc/self/mountinfo";
        try
        {
            return ResolveLinuxDeviceId(fullPath, File.ReadLines(mountInfoPath));
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveLinuxDeviceId(string fullPath, IEnumerable<string> mountInfoLines)
    {
        string? bestDeviceId = null;
        var bestMountPointLength = -1;
        foreach (var line in mountInfoLines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 5)
            {
                continue;
            }

            var mountPoint = Path.TrimEndingDirectorySeparator(DecodeMountInfoPath(fields[4]));
            if (mountPoint.Length <= bestMountPointLength
                || !IsPathWithin(fullPath, mountPoint, StringComparison.Ordinal))
            {
                continue;
            }

            bestDeviceId = fields[2];
            bestMountPointLength = mountPoint.Length;
        }

        return bestDeviceId;
    }

    private static bool IsPathWithin(string path, string root, StringComparison comparison)
    {
        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        var rootWithSeparator = root == Path.DirectorySeparatorChar.ToString()
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison);
    }

    private static string DecodeMountInfoPath(string value)
    {
        return value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
    }
}
