using System.Text;
using Means.Core;
using Means.Infrastructure.SqliteFs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Means.IntegrationTests;

public sealed class SqliteFsReplicaTests
{
    [Fact]
    public async Task PutObjectWritesReplicasReadsFallbackAndDeletesReplicaFiles()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 2);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("replicas", CancellationToken.None);

        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "replicas",
                "hello.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("replicated content")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        var firstReplica = ObjectPath(diskA, info.ObjectId);
        var secondReplica = ObjectPath(diskB, info.ObjectId);
        Assert.True(File.Exists(firstReplica));
        Assert.True(File.Exists(secondReplica));

        File.Delete(firstReplica);
        await using (var data = await store.GetObjectAsync("replicas", "hello.txt", CancellationToken.None))
        using (var reader = new StreamReader(data.Content, Encoding.UTF8))
        {
            Assert.Equal("replicated content", await reader.ReadToEndAsync());
        }

        var queued = await store.EnqueueMissingReplicaRepairsAsync(CancellationToken.None);
        var repaired = await store.RepairQueuedReplicasAsync(10, CancellationToken.None);
        Assert.Equal(1, queued);
        Assert.Equal(1, repaired);
        Assert.True(File.Exists(firstReplica));
        Assert.Equal("replicated content", await File.ReadAllTextAsync(firstReplica));

        await store.DeleteObjectAsync("replicas", "hello.txt", CancellationToken.None);
        Assert.False(File.Exists(firstReplica));
        Assert.False(File.Exists(secondReplica));
    }

    [Fact]
    public async Task CompleteMultipartUploadWritesObjectReplicas()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 2);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("replicas", CancellationToken.None);

        var upload = await store.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest(
                "replicas",
                "multipart.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var part = await store.UploadPartAsync(
            new UploadPartRequest(
                "replicas",
                "multipart.bin",
                upload.UploadId,
                1,
                new MemoryStream(Encoding.UTF8.GetBytes("multipart replicated"))),
            CancellationToken.None);

        var completed = await store.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest(
                "replicas",
                "multipart.bin",
                upload.UploadId,
                [new CompletedMultipartPart(1, part.ETag)]),
            CancellationToken.None);

        Assert.True(File.Exists(ObjectPath(diskA, completed.ObjectId)));
        Assert.True(File.Exists(ObjectPath(diskB, completed.ObjectId)));
    }

    [Fact]
    public async Task DiskHealthDetectionIsolatesMissingDiskAndRestoresHealthyDisk()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        Directory.CreateDirectory(diskA);
        var store = CreateStore(root, replicaCount: 1);
        await RegisterReplicaDisksAsync(store, diskA, diskB);

        var isolated = await store.DetectAndIsolateFailedDisksAsync(CancellationToken.None);
        var topologyAfterIsolation = await store.GetClusterTopologyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        var nodeAfterIsolation = Assert.Single(topologyAfterIsolation.Nodes);
        Assert.True(isolated >= 1);
        Assert.Equal(StorageDiskStatuses.Online, Assert.Single(nodeAfterIsolation.Disks, disk => disk.DiskId == "disk-a").Status);
        Assert.Equal(StorageDiskStatuses.Offline, Assert.Single(nodeAfterIsolation.Disks, disk => disk.DiskId == "disk-b").Status);

        Directory.CreateDirectory(diskB);
        var restored = await store.DetectAndIsolateFailedDisksAsync(CancellationToken.None);
        var topologyAfterRestore = await store.GetClusterTopologyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        var nodeAfterRestore = Assert.Single(topologyAfterRestore.Nodes);
        Assert.True(restored >= 1);
        Assert.All(nodeAfterRestore.Disks, disk => Assert.Equal(StorageDiskStatuses.Online, disk.Status));
    }

    [Fact]
    public async Task ErasureCodingWritesReadsFallbackRebuildsMissingShardAndDeletesFiles()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var diskC = Path.Combine(root, "disk-c");
        var store = CreateStore(root, replicaCount: 1);
        await RegisterReplicaDisksAsync(store, diskA, diskB, diskC);
        await store.SaveErasureCodingProfileAsync(
            new ErasureCodingProfile(
                "ec-test",
                DataShards: 2,
                ParityShards: 1,
                CellSizeBytes: 65536,
                Enabled: true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        await store.CreateBucketAsync("ec", CancellationToken.None);

        var expected = "erasure-coded object content";
        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "ec",
                "object.bin",
                new MemoryStream(Encoding.UTF8.GetBytes(expected)),
                "application/octet-stream",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        var shardFiles = EcShardFiles(root, info.ObjectId);
        Assert.Equal(3, shardFiles.Length);

        foreach (var path in ReplicaObjectFiles(root, info.ObjectId))
        {
            File.Delete(path);
        }

        await using (var data = await store.GetObjectAsync("ec", "object.bin", CancellationToken.None))
        using (var reader = new StreamReader(data.Content, Encoding.UTF8))
        {
            Assert.Equal(expected, await reader.ReadToEndAsync());
        }

        var missingDataShard = Assert.Single(shardFiles, path => path.EndsWith(".00.ec", StringComparison.Ordinal));
        File.Delete(missingDataShard);
        var rebuilt = await store.RebuildErasureCodedObjectsAsync(10, CancellationToken.None);
        Assert.Equal(1, rebuilt);
        Assert.True(File.Exists(missingDataShard));

        await store.DeleteObjectAsync("ec", "object.bin", CancellationToken.None);
        Assert.Empty(EcShardFiles(root, info.ObjectId));
    }

    [Fact]
    public async Task ErasureCodingReconstructsMultipleMissingDataShardsWhenParityAllows()
    {
        var root = TestRoot();
        var store = CreateStore(root, replicaCount: 1);
        var disks = Enumerable.Range(0, 5)
            .Select(index => ($"disk-{index}", Path.Combine(root, $"disk-{index}")))
            .ToArray();
        await RegisterReplicaDisksAsync(store, disks);
        await store.SaveErasureCodingProfileAsync(
            new ErasureCodingProfile(
                "ec-3-2",
                DataShards: 3,
                ParityShards: 2,
                CellSizeBytes: 65536,
                Enabled: true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        await store.CreateBucketAsync("ec-multi", CancellationToken.None);

        var expected = "abcdefghijklmnopqrstuvwxyz0123456789";
        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "ec-multi",
                "object.bin",
                new MemoryStream(Encoding.UTF8.GetBytes(expected)),
                "application/octet-stream",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        foreach (var path in ReplicaObjectFiles(root, info.ObjectId))
        {
            File.Delete(path);
        }

        var shardFiles = EcShardFiles(root, info.ObjectId);
        foreach (var dataShard in shardFiles.Where(path =>
            path.EndsWith(".00.ec", StringComparison.Ordinal) ||
            path.EndsWith(".01.ec", StringComparison.Ordinal)))
        {
            File.Delete(dataShard);
        }

        await using (var data = await store.GetObjectAsync("ec-multi", "object.bin", CancellationToken.None))
        using (var reader = new StreamReader(data.Content, Encoding.UTF8))
        {
            Assert.Equal(expected, await reader.ReadToEndAsync());
        }

        var rebuilt = await store.RebuildErasureCodedObjectsAsync(10, CancellationToken.None);
        Assert.Equal(2, rebuilt);
        Assert.Equal(5, EcShardFiles(root, info.ObjectId).Length);
    }

    [Fact]
    public async Task PutObjectIdempotencyReturnsOriginalResultAndRejectsDifferentPayload()
    {
        var root = TestRoot();
        var store = CreateStore(root, replicaCount: 1);
        await store.CreateBucketAsync("idempotent", CancellationToken.None);

        var first = await store.PutObjectAsync(
            new PutObjectRequest(
                "idempotent",
                "same-key.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("same content")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null,
                IdempotencyKey: "request-1"),
            CancellationToken.None);
        var replay = await store.PutObjectAsync(
            new PutObjectRequest(
                "idempotent",
                "same-key.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("same content")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null,
                IdempotencyKey: "request-1"),
            CancellationToken.None);

        Assert.Equal(first.ObjectId, replay.ObjectId);
        Assert.Equal(first.ETag, replay.ETag);

        var ex = await Assert.ThrowsAsync<MeansException>(() => store.PutObjectAsync(
            new PutObjectRequest(
                "idempotent",
                "same-key.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("different content")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null,
                IdempotencyKey: "request-1"),
            CancellationToken.None));
        Assert.Equal(MeansErrorCodes.InvalidRequest, ex.Code);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task MetadataMigrationsSnapshotsAndMonotonicTimestampsAreApplied()
    {
        var root = TestRoot();
        var store = CreateStore(root, replicaCount: 1);
        await store.CreateBucketAsync("metadata", CancellationToken.None);

        var first = await store.PutObjectAsync(
            new PutObjectRequest(
                "metadata",
                "a.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("a")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var second = await store.PutObjectAsync(
            new PutObjectRequest(
                "metadata",
                "a.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("b")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        Assert.True(second.LastModified > first.LastModified);
        var migrations = await store.ListSchemaMigrationsAsync(CancellationToken.None);
        Assert.Contains(migrations, migration => migration.MigrationId == "0005-metadata-consistency-and-maintenance");

        var snapshot = await store.CreateMetadataSnapshotAsync(Path.Combine(root, "snapshots", "metadata.db"), CancellationToken.None);
        Assert.True(File.Exists(snapshot.SnapshotPath));
        await store.PutObjectAsync(
            new PutObjectRequest(
                "metadata",
                "after-snapshot.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("later")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        await store.RestoreMetadataSnapshotAsync(snapshot.SnapshotPath, CancellationToken.None);
        await store.HeadObjectAsync("metadata", "a.txt", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MeansException>(() => store.HeadObjectAsync("metadata", "after-snapshot.txt", CancellationToken.None));
        Assert.Equal(MeansErrorCodes.NoSuchKey, ex.Code);
    }

    [Fact]
    public async Task MetadataConsistencyCheckRepairsMissingCurrentVersionAndQueuesReplicaRepair()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 2);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("consistency", CancellationToken.None);

        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "consistency",
                "object.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("self-healing metadata")),
                "text/plain",
                new Dictionary<string, string> { ["owner"] = "test" },
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var replicaFiles = ReplicaObjectFiles(root, info.ObjectId);
        Assert.Equal(2, replicaFiles.Length);
        File.Delete(replicaFiles[0]);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "means.db"),
            ForeignKeys = true
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "delete from object_versions where object_id = $objectId;";
            command.Parameters.AddWithValue("$objectId", info.ObjectId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var result = await store.CheckMetadataConsistencyAsync(repair: true, maxItems: 100, CancellationToken.None);

        Assert.Equal(1, result.CheckedObjectCount);
        Assert.Equal(1, result.MissingCurrentVersionCount);
        Assert.Equal(1, result.RepairedCurrentVersionCount);
        Assert.Equal(1, result.MissingReplicaFileCount);
        Assert.Equal(1, result.QueuedReplicaRepairCount);
        Assert.Equal(0, result.OrphanedReplicaRecordCount);

        var restoredVersion = await store.HeadObjectAsync("consistency", "object.txt", info.ObjectId, CancellationToken.None);
        Assert.Equal(info.ObjectId, restoredVersion.ObjectId);
        Assert.Equal("test", restoredVersion.Metadata["owner"]);

        var repaired = await store.RepairQueuedReplicasAsync(10, CancellationToken.None);
        Assert.Equal(1, repaired);
        Assert.Equal(2, ReplicaObjectFiles(root, info.ObjectId).Length);

        var clean = await store.CheckMetadataConsistencyAsync(repair: false, maxItems: 100, CancellationToken.None);
        Assert.Equal(0, clean.MissingCurrentVersionCount);
        Assert.Equal(0, clean.MissingReplicaFileCount);
        Assert.Equal(0, clean.OrphanedReplicaRecordCount);
    }

    [Fact]
    public async Task StorageGarbageCollectionDryRunsAndDeletesOnlyUnreferencedFiles()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 2);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("gc", CancellationToken.None);

        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "gc",
                "keep.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("keep me")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var referencedReplicas = await ReplicaPathsByIndexAsync(root, info.ObjectId);
        Assert.Equal(2, referencedReplicas.Length);

        var orphanReplicaId = "aa" + Guid.NewGuid().ToString("N")[2..];
        var orphanReplicaPath = ObjectPath(diskA, orphanReplicaId);
        WriteGarbageFile(orphanReplicaPath);

        var orphanFallbackId = "bb" + Guid.NewGuid().ToString("N")[2..];
        var orphanFallbackPath = ObjectPath(Path.Combine(root, "objects"), orphanFallbackId);
        WriteGarbageFile(orphanFallbackPath);

        var orphanPartId = "cc" + Guid.NewGuid().ToString("N")[2..];
        var orphanPartPath = Path.Combine(root, "objects", "multipart", orphanPartId[..2], orphanPartId);
        WriteGarbageFile(orphanPartPath);

        var orphanEcId = "dd" + Guid.NewGuid().ToString("N")[2..];
        var orphanEcPath = Path.Combine(diskB, "ec", orphanEcId[..2], orphanEcId + ".00.ec");
        WriteGarbageFile(orphanEcPath);

        var staleTempPath = Path.Combine(root, "objects", "tmp", Guid.NewGuid().ToString("N") + ".tmp");
        WriteGarbageFile(staleTempPath);

        var dryRun = await store.CollectStorageGarbageAsync(delete: false, maxFiles: 100, CancellationToken.None);

        Assert.False(dryRun.DeleteEnabled);
        Assert.Equal(0, dryRun.DeletedFileCount);
        Assert.True(dryRun.OrphanedObjectReplicaFileCount >= 1);
        Assert.True(dryRun.OrphanedFallbackObjectFileCount >= 1);
        Assert.True(dryRun.OrphanedMultipartPartFileCount >= 1);
        Assert.True(dryRun.OrphanedErasureCodingShardFileCount >= 1);
        Assert.True(dryRun.ExpiredTempFileCount >= 1);
        Assert.True(File.Exists(orphanReplicaPath));
        Assert.True(File.Exists(orphanFallbackPath));
        Assert.True(File.Exists(orphanPartPath));
        Assert.True(File.Exists(orphanEcPath));
        Assert.True(File.Exists(staleTempPath));

        var deleted = await store.CollectStorageGarbageAsync(delete: true, maxFiles: 100, CancellationToken.None);

        Assert.True(deleted.DeleteEnabled);
        Assert.True(deleted.DeletedFileCount >= 5);
        Assert.Equal(0, deleted.FailedDeleteCount);
        Assert.False(File.Exists(orphanReplicaPath));
        Assert.False(File.Exists(orphanFallbackPath));
        Assert.False(File.Exists(orphanPartPath));
        Assert.False(File.Exists(orphanEcPath));
        Assert.False(File.Exists(staleTempPath));
        Assert.All(referencedReplicas, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task ChecksumVerifiedReadsSkipCorruptReplicaAndRepairIt()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 2, verifyChecksumOnRead: true);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("checksum", CancellationToken.None);

        var expected = "checksum protected content";
        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "checksum",
                "object.txt",
                new MemoryStream(Encoding.UTF8.GetBytes(expected)),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var replicaPaths = await ReplicaPathsByIndexAsync(root, info.ObjectId);
        Assert.Equal(2, replicaPaths.Length);
        await File.WriteAllTextAsync(replicaPaths[0], "corrupted", CancellationToken.None);

        await using (var data = await store.GetObjectAsync("checksum", "object.txt", CancellationToken.None))
        using (var reader = new StreamReader(data.Content, Encoding.UTF8))
        {
            Assert.Equal(expected, await reader.ReadToEndAsync());
        }

        var repaired = await store.RepairQueuedReplicasAsync(10, CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal(expected, await File.ReadAllTextAsync(replicaPaths[0], CancellationToken.None));
        Assert.Equal(expected, await File.ReadAllTextAsync(replicaPaths[1], CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleRulesExpireCurrentObjectsNoncurrentVersionsAndMultipartUploads()
    {
        var root = TestRoot();
        var store = CreateStore(root, replicaCount: 1);
        await store.CreateBucketAsync("lifecycle", CancellationToken.None);
        await store.PutBucketVersioningAsync("lifecycle", BucketVersioningStatuses.Enabled, CancellationToken.None);
        await store.PutBucketLifecycleAsync(
            "lifecycle",
            new BucketLifecycleConfiguration(
            [
                new LifecycleRule(
                    "cleanup",
                    "Enabled",
                    "tmp/",
                    ExpirationDays: 1,
                    NoncurrentVersionExpirationDays: 1,
                    AbortIncompleteMultipartUploadDays: 1)
            ]),
            CancellationToken.None);

        var first = await store.PutObjectAsync(
            new PutObjectRequest(
                "lifecycle",
                "tmp/versioned.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("first")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        await store.PutObjectAsync(
            new PutObjectRequest(
                "lifecycle",
                "tmp/versioned.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("second")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var expiring = await store.PutObjectAsync(
            new PutObjectRequest(
                "lifecycle",
                "tmp/current.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("current")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var upload = await store.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest(
                "lifecycle",
                "tmp/incomplete.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);

        var applied = await store.ApplyLifecycleRulesAsync(DateTimeOffset.UtcNow.AddDays(2), 100, CancellationToken.None);

        Assert.True(applied >= 3);
        var deletedCurrent = await Assert.ThrowsAsync<MeansException>(() => store.HeadObjectAsync("lifecycle", "tmp/current.txt", CancellationToken.None));
        Assert.Equal(MeansErrorCodes.NoSuchKey, deletedCurrent.Code);
        var deletedVersion = await Assert.ThrowsAsync<MeansException>(() => store.HeadObjectAsync("lifecycle", "tmp/versioned.txt", first.ObjectId, CancellationToken.None));
        Assert.Equal(MeansErrorCodes.NoSuchVersion, deletedVersion.Code);
        var abortedUpload = await Assert.ThrowsAsync<MeansException>(() => store.ListPartsAsync("lifecycle", "tmp/incomplete.bin", upload.UploadId, 0, 1000, CancellationToken.None));
        Assert.Equal(MeansErrorCodes.NoSuchUpload, abortedUpload.Code);
    }

    [Fact]
    public async Task RebalanceMigratesReplicaAwayFromOfflineDisk()
    {
        var root = TestRoot();
        var diskA = Path.Combine(root, "disk-a");
        var diskB = Path.Combine(root, "disk-b");
        var store = CreateStore(root, replicaCount: 1);
        await RegisterReplicaDisksAsync(store, diskA, diskB);
        await store.CreateBucketAsync("rebalance", CancellationToken.None);

        var info = await store.PutObjectAsync(
            new PutObjectRequest(
                "rebalance",
                "object.txt",
                new MemoryStream(Encoding.UTF8.GetBytes("rebalance me")),
                "text/plain",
                new Dictionary<string, string>(),
                CacheControl: null,
                ContentDisposition: null),
            CancellationToken.None);
        var sourceDisk = File.Exists(ObjectPath(diskA, info.ObjectId)) ? diskA : diskB;
        var targetDisk = sourceDisk == diskA ? diskB : diskA;

        await store.HeartbeatNodeAsync(
            new ClusterNodeHeartbeat(
                "replica-node",
                [
                    new StorageDiskHeartbeat(
                        targetDisk == diskA ? "disk-a" : "disk-b",
                        1_000_000,
                        1_000_000,
                        StorageDiskStatuses.Online,
                        DateTimeOffset.UtcNow)
                ],
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var migrated = await store.RebalanceObjectReplicasAsync(10, CancellationToken.None);

        Assert.Equal(1, migrated);
        Assert.False(File.Exists(ObjectPath(sourceDisk, info.ObjectId)));
        Assert.True(File.Exists(ObjectPath(targetDisk, info.ObjectId)));
    }

    private static SqliteFsStore CreateStore(string root, int replicaCount, bool verifyChecksumOnRead = false)
    {
        return new SqliteFsStore(
            Options.Create(new SqliteFsOptions
            {
                DatabasePath = Path.Combine(root, "means.db"),
                ObjectsPath = Path.Combine(root, "objects"),
                ReplicaCount = replicaCount,
                ReplicaOfflineAfterSeconds = 60,
                VerifyChecksumOnRead = verifyChecksumOnRead
            }),
            new DeterministicObjectPlacementPlanner("replica-test"));
    }

    private static async Task RegisterReplicaDisksAsync(SqliteFsStore store, string diskA, string diskB)
    {
        await RegisterReplicaDisksAsync(store, [("disk-a", diskA), ("disk-b", diskB)]);
    }

    private static async Task RegisterReplicaDisksAsync(SqliteFsStore store, string diskA, string diskB, string diskC)
    {
        await RegisterReplicaDisksAsync(store, [("disk-a", diskA), ("disk-b", diskB), ("disk-c", diskC)]);
    }

    private static async Task RegisterReplicaDisksAsync(SqliteFsStore store, IReadOnlyList<(string DiskId, string Path)> disks)
    {
        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "replica-cluster",
                "Replica Cluster",
                "replica-node",
                "replica-node",
                "http://replica-node",
                "replica-pool",
                "Replica Pool",
                disks
                    .Select(disk => new StorageDiskRegistration(disk.DiskId, "replica-pool", disk.Path, 1_000_000, 1_000_000, StorageDiskStatuses.Online))
                    .ToArray(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    private static string ObjectPath(string diskPath, string objectId)
    {
        return Path.Combine(diskPath, objectId[..2], objectId);
    }

    private static string[] ReplicaObjectFiles(string root, string objectId)
    {
        return Directory.Exists(root)
            ? Directory.GetFiles(root, objectId, SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ec{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
    }

    private static string[] EcShardFiles(string root, string objectId)
    {
        return Directory.Exists(root)
            ? Directory.GetFiles(root, objectId + ".*.ec", SearchOption.AllDirectories)
            : [];
    }

    private static async Task<string[]> ReplicaPathsByIndexAsync(string root, string objectId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "means.db"),
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select content_path
            from object_replicas
            where object_id = $objectId
            order by replica_index;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync();
        var paths = new List<string>();
        while (await reader.ReadAsync())
        {
            paths.Add(reader.GetString(0));
        }

        return paths.ToArray();
    }

    private static void WriteGarbageFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "garbage");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.Subtract(TimeSpan.FromHours(2)));
    }

    private static string TestRoot()
    {
        return Path.Combine(Path.GetTempPath(), "means-replica-tests", Guid.NewGuid().ToString("N"));
    }
}
