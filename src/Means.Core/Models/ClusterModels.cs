namespace Means.Core;

public static class ClusterNodeStatuses
{
    public const string Online = "Online";
    public const string Offline = "Offline";
}

public static class StorageDiskStatuses
{
    public const string Online = "Online";
    public const string Offline = "Offline";
}

public sealed record StorageClusterInfo(
    string ClusterId,
    string Name,
    DateTimeOffset CreatedAt);

public sealed record StoragePoolInfo(
    string PoolId,
    string ClusterId,
    string Name,
    DateTimeOffset CreatedAt,
    int NodeCount,
    int DiskCount,
    long TotalBytes,
    long AvailableBytes);

public sealed record StorageDiskInfo(
    string DiskId,
    string NodeId,
    string PoolId,
    string MountPath,
    long TotalBytes,
    long AvailableBytes,
    string Status,
    DateTimeOffset LastSeenAt);

public sealed record ClusterNodeInfo(
    string NodeId,
    string ClusterId,
    string HostName,
    string Endpoint,
    string Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeatAt,
    IReadOnlyList<StorageDiskInfo> Disks);

public sealed record ClusterTopology(
    StorageClusterInfo Cluster,
    IReadOnlyList<ClusterNodeInfo> Nodes,
    IReadOnlyList<StoragePoolInfo> Pools);

public sealed record ClusterNodeRegistration(
    string ClusterId,
    string ClusterName,
    string NodeId,
    string HostName,
    string Endpoint,
    string PoolId,
    string PoolName,
    IReadOnlyList<StorageDiskRegistration> Disks,
    DateTimeOffset RegisteredAt);

public sealed record StorageDiskRegistration(
    string DiskId,
    string PoolId,
    string MountPath,
    long TotalBytes,
    long AvailableBytes,
    string Status);

public sealed record ClusterNodeHeartbeat(
    string NodeId,
    IReadOnlyList<StorageDiskHeartbeat> Disks,
    DateTimeOffset HeartbeatAt);

public sealed record StorageDiskHeartbeat(
    string DiskId,
    long TotalBytes,
    long AvailableBytes,
    string Status,
    DateTimeOffset LastSeenAt);
