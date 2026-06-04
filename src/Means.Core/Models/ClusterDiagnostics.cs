namespace Means.Core;

public sealed record ClusterDiagnostics(
    DateTimeOffset GeneratedAt,
    ClusterDiagnosticsSummary Summary,
    ClusterTopology Topology,
    ObjectReplicaDiagnostics ObjectReplicas,
    ReplicaRepairQueueDiagnostics RepairQueue,
    MetadataDiagnostics Metadata,
    ErasureCodingDiagnostics ErasureCoding,
    ClusterInternalTransportDiagnostics InternalTransport,
    IReadOnlyList<BackgroundTaskSnapshot> BackgroundTasks);

public sealed record ClusterDiagnosticsSummary(
    long BucketCount,
    long ObjectCount,
    long TotalObjectBytes,
    int NodeCount,
    int OnlineNodeCount,
    int OfflineNodeCount,
    int PoolCount,
    int DiskCount,
    int OnlineDiskCount,
    int OfflineDiskCount,
    long TotalCapacityBytes,
    long AvailableCapacityBytes,
    long UsedCapacityBytes);

public sealed record ObjectReplicaDiagnostics(
    int DesiredReplicaCount,
    long ObjectCount,
    long ReplicaRecordCount,
    long CommittedReplicaRecordCount,
    long ExistingReplicaFileCount,
    long MissingReplicaFileCount,
    long MissingReplicaObjectCount,
    long UnderReplicatedObjectCount,
    long ObjectsWithoutReplicaManifestCount);

public sealed record ReplicaRepairQueueDiagnostics(
    long TotalCount,
    long PendingCount,
    long CompletedCount,
    long FailedCount,
    long RetryableFailedCount,
    long MaxAttemptsReachedCount,
    DateTimeOffset? OldestPendingAt,
    DateTimeOffset? LastUpdatedAt,
    IReadOnlyList<ReplicaRepairQueueStatusDiagnostics> Statuses,
    IReadOnlyList<ReplicaRepairQueueItemDiagnostics> Items);

public sealed record ReplicaRepairQueueStatusDiagnostics(string Status, long Count);

public sealed record ReplicaRepairQueueItemDiagnostics(
    string BucketName,
    string Key,
    string ObjectId,
    string Reason,
    string Status,
    int AttemptCount,
    DateTimeOffset QueuedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError);

public sealed record MetadataDiagnostics(
    long PendingCommitCount,
    long OrphanedReplicaRecordCount);

public sealed record ErasureCodingDiagnostics(
    long ProfileCount,
    long EnabledProfileCount,
    long DisabledProfileCount);

public sealed record ClusterInternalTransportDiagnostics(
    bool ShardRpcEnabled,
    long MaxShardTransferBytes);
