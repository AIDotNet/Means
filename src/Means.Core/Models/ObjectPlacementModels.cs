namespace Means.Core;

public sealed record ObjectPlacementRequest(
    string BucketName,
    string ObjectKey,
    string? VersionId,
    int ReplicaCount,
    long ContentLength = 0,
    string? PoolId = null);

public sealed record ObjectPlacementPlan(
    string BucketName,
    string ObjectKey,
    string? VersionId,
    IReadOnlyList<ObjectPlacementReplica> Replicas);

public sealed record ObjectPlacementReplica(
    int ReplicaIndex,
    string NodeId,
    string DiskId,
    string PoolId,
    string MountPath);
