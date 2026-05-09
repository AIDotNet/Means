using Means.Core;

namespace Means.Services;

public static class BackgroundTaskDescriptors
{
    public static BackgroundTaskDescriptor ClusterHeartbeat(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "cluster-heartbeat",
            "Local cluster node heartbeat",
            "cluster",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor DiskHealthIsolation(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "disk-health-isolation",
            "Disk health isolation",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor ReplicaRepair(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "replica-repair",
            "Replica repair",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor ErasureCodingRepair(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "erasure-coding-repair",
            "Erasure coding shard repair",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor ReplicaRebalance(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "replica-rebalance",
            "Replica rebalance",
            "rebalance",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor MultipartCleanup(int intervalMinutes)
    {
        return new BackgroundTaskDescriptor(
            "multipart-cleanup",
            "Abandoned multipart upload cleanup",
            "lifecycle",
            Math.Max(1, intervalMinutes) * 60);
    }

    public static BackgroundTaskDescriptor Lifecycle(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "s3-lifecycle",
            "S3 lifecycle rules",
            "lifecycle",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor ObjectScrub(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "object-scrub",
            "Object checksum scrub",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor MetadataConsistency(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "metadata-consistency",
            "Metadata consistency check",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor StorageGarbageCollection(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "storage-garbage-collection",
            "Storage garbage collection",
            "repair",
            Math.Max(5, intervalSeconds));
    }

    public static BackgroundTaskDescriptor ReplicationWorker(int intervalSeconds)
    {
        return new BackgroundTaskDescriptor(
            "replication-worker",
            "Replication worker",
            "replication",
            Math.Max(5, intervalSeconds));
    }
}
