namespace Means.Core;

/// <summary>
/// Cluster metadata boundary used by distributed storage control-plane features.
/// The single-node SQLite adapter implements this first so higher layers can move
/// from local topology to real multi-node placement without changing API shape.
/// </summary>
public interface IClusterStore
{
    Task RegisterNodeAsync(ClusterNodeRegistration registration, CancellationToken cancellationToken);

    Task HeartbeatNodeAsync(ClusterNodeHeartbeat heartbeat, CancellationToken cancellationToken);

    Task<ClusterTopology> GetClusterTopologyAsync(DateTimeOffset offlineBeforeUtc, CancellationToken cancellationToken);
}
