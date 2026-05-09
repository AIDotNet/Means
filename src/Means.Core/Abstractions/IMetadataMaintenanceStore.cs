namespace Means.Core;

/// <summary>
/// Maintenance boundary for metadata durability features.
/// Keeping it separate from the object data-plane port makes the SQLite metadata backend replaceable
/// by a distributed metadata service without changing S3 request handlers.
/// </summary>
public interface IMetadataMaintenanceStore
{
    Task<IReadOnlyList<SchemaMigrationInfo>> ListSchemaMigrationsAsync(CancellationToken cancellationToken);

    Task<MetadataSnapshotInfo> CreateMetadataSnapshotAsync(string snapshotPath, CancellationToken cancellationToken);

    Task<MetadataSnapshotInfo> RestoreMetadataSnapshotAsync(string snapshotPath, CancellationToken cancellationToken);

    Task<MetadataConsistencyCheckResult> CheckMetadataConsistencyAsync(
        bool repair,
        int maxItems,
        CancellationToken cancellationToken);

    Task<StorageGarbageCollectionResult> CollectStorageGarbageAsync(
        bool delete,
        int maxFiles,
        CancellationToken cancellationToken);
}
