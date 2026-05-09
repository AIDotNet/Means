namespace Means.Core;

public interface IStorageMaintenanceOperations
{
    Task<int> DetectAndIsolateFailedDisksAsync(CancellationToken cancellationToken);

    Task<int> EnqueueMissingReplicaRepairsAsync(CancellationToken cancellationToken);

    Task<int> RepairQueuedReplicasAsync(int maxItems, CancellationToken cancellationToken);

    Task<int> RebuildErasureCodedObjectsAsync(int maxItems, CancellationToken cancellationToken);

    Task<int> RebalanceObjectReplicasAsync(int maxItems, CancellationToken cancellationToken);

    Task<int> ApplyLifecycleRulesAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken);

    Task<ObjectScrubResult> ScrubObjectReplicasAsync(int maxItems, CancellationToken cancellationToken);

    Task<MetadataConsistencyCheckResult> CheckMetadataConsistencyAsync(
        bool repair,
        int maxItems,
        CancellationToken cancellationToken);

    Task<StorageGarbageCollectionResult> CollectStorageGarbageAsync(
        bool delete,
        int maxFiles,
        CancellationToken cancellationToken);

    Task<int> CleanupMultipartUploadsAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}
