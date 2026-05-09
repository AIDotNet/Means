namespace Means.Core;

public sealed record SchemaMigrationInfo(
    string MigrationId,
    DateTimeOffset AppliedAt);

public sealed record MetadataSnapshotInfo(
    string SnapshotPath,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record MetadataConsistencyCheckResult(
    long CheckedObjectCount,
    long MissingCurrentVersionCount,
    long RepairedCurrentVersionCount,
    long MissingReplicaManifestCount,
    long UnderReplicatedObjectCount,
    long MissingReplicaFileCount,
    long QueuedReplicaRepairCount,
    long OrphanedReplicaRecordCount,
    long PendingMetadataCommitCount);

public sealed record StorageGarbageCollectionResult(
    bool DeleteEnabled,
    bool LimitReached,
    long ScannedFileCount,
    long CandidateFileCount,
    long DeletedFileCount,
    long FailedDeleteCount,
    long OrphanedObjectReplicaFileCount,
    long OrphanedFallbackObjectFileCount,
    long OrphanedMultipartPartFileCount,
    long OrphanedErasureCodingShardFileCount,
    long ExpiredTempFileCount);
