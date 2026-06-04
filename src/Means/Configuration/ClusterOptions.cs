namespace Means.Configuration;

public sealed class ClusterOptions
{
    public string ClusterId { get; set; } = "local";

    public string ClusterName { get; set; } = "Local Means Cluster";

    public string NodeId { get; set; } = "";

    public string NodeEndpoint { get; set; } = "http://localhost";

    public string PoolId { get; set; } = "pool-1";

    public string PoolName { get; set; } = "Pool 1";

    public string ObjectDiskId { get; set; } = "local-objects";

    public string PlacementSeed { get; set; } = "means-v1";

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    public int OfflineAfterSeconds { get; set; } = 60;

    public int DiskHealthIntervalSeconds { get; set; } = 30;

    public string InternalAuthToken { get; set; } = "";

    public long MaxShardTransferBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}
