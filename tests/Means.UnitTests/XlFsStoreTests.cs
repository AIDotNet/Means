using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Means.Core;
using Means.Infrastructure.XlFs;
using Microsoft.Extensions.Options;

namespace Means.UnitTests;

public sealed class XlFsStoreTests
{
    [Fact]
    public async Task LogDbReplaysWalAndTruncatesPartialRecord()
    {
        var root = CreateTempRoot();
        try
        {
            await using (var db = await MeansLogDb.OpenAsync(root, CancellationToken.None))
            {
                await db.PutJsonAsync("b:alpha", "one", CancellationToken.None);
                await db.PutBatchAsync([
                    new LogDbMutation("b:beta", Encoding.UTF8.GetBytes("two"), false),
                    new LogDbMutation("b:deleted", null, true)
                ], CancellationToken.None);
            }

            await File.AppendAllBytesAsync(Path.Combine(root, "current.wal"), [0x01, 0x02, 0x03, 0x04], CancellationToken.None);

            await using var reopened = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            Assert.Equal("one", await reopened.GetJsonAsync<string>("b:alpha", CancellationToken.None));
            Assert.Equal("two", Encoding.UTF8.GetString((await reopened.GetAsync("b:beta", CancellationToken.None))!));
            Assert.Null(await reopened.GetAsync("b:deleted", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbOpenReportsWhenWalIsAlreadyInUse()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var walPath = Path.Combine(root, "current.wal");
            await File.WriteAllBytesAsync(walPath, [], CancellationToken.None);

            await using var lockStream = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var ex = await Assert.ThrowsAsync<IOException>(() => MeansLogDb.OpenAsync(root, CancellationToken.None));

            Assert.Contains(root, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Another Means process may already be using", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbReplaysLegacyJsonWal()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new
                {
                    Key = "b:legacy",
                    Delete = false,
                    Value = Convert.ToBase64String(Encoding.UTF8.GetBytes("old"))
                }
            });
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x4d4c4442);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), Crc32(payload));
            await File.WriteAllBytesAsync(Path.Combine(root, "current.wal"), header.Concat(payload).ToArray(), CancellationToken.None);

            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);

            Assert.Equal("old", Encoding.UTF8.GetString((await db.GetAsync("b:legacy", CancellationToken.None))!));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbSnapshotRestoreReplacesCurrentIndex()
    {
        var root = CreateTempRoot();
        var snapshot = Path.Combine(CreateTempRoot(), "snapshot.json");
        try
        {
            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            await db.PutJsonAsync("k", "before", CancellationToken.None);
            await db.CreateSnapshotAsync(snapshot, CancellationToken.None);
            await db.PutJsonAsync("k", "after", CancellationToken.None);

            await db.RestoreSnapshotAsync(snapshot, CancellationToken.None);

            Assert.Equal("before", await db.GetJsonAsync<string>("k", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteTempRoot(Path.GetDirectoryName(snapshot)!);
        }
    }

    [Fact]
    public async Task LogDbPrefixScanSeeksWithinSortedKeyIndex()
    {
        var root = CreateTempRoot();
        try
        {
            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            await db.PutBatchAsync([
                new LogDbMutation("a:outside", Encoding.UTF8.GetBytes("0"), false),
                new LogDbMutation("b:001", Encoding.UTF8.GetBytes("1"), false),
                new LogDbMutation("b:002", Encoding.UTF8.GetBytes("2"), false),
                new LogDbMutation("b:003", Encoding.UTF8.GetBytes("3"), false),
                new LogDbMutation("c:outside", Encoding.UTF8.GetBytes("4"), false)
            ], CancellationToken.None);

            var firstPage = await db.ScanPrefixAsync("b:", 2, null, CancellationToken.None);
            Assert.Equal(["b:001", "b:002"], firstPage.Select(pair => pair.Key).ToArray());

            var secondPage = await db.ScanPrefixAsync("b:", 2, firstPage[^1].Key, CancellationToken.None);
            Assert.Equal(["b:003"], secondPage.Select(pair => pair.Key).ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbStatsReportSyncModeWalSizeAndKeyCount()
    {
        var root = CreateTempRoot();
        try
        {
            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None, XlMetaSyncModes.Batch);
            await db.PutBatchAsync([
                new LogDbMutation("stats:a", Encoding.UTF8.GetBytes("1"), false),
                new LogDbMutation("stats:b", Encoding.UTF8.GetBytes("2"), false)
            ], CancellationToken.None);

            var stats = db.GetStats();

            Assert.Equal(XlMetaSyncModes.Batch, stats.SyncMode);
            Assert.Equal(2, stats.KeyCount);
            Assert.True(stats.WalBytes > 0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsObjectLifecycleUsesDiskFormatManifestAndLogDb()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("docs", CancellationToken.None);

            var body = "hello xlfs";
            var info = await store.PutObjectAsync(new PutObjectRequest(
                "docs",
                "hello.txt",
                new MemoryStream(Encoding.UTF8.GetBytes(body)),
                "text/plain",
                new Dictionary<string, string> { ["origin"] = "unit" },
                null,
                null), CancellationToken.None);

            Assert.Equal(body.Length, info.ContentLength);
            Assert.True(File.Exists(Path.Combine(root, "disk1", ".means.sys", "format.json")));
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, "disk1", "objects"), "xl.meta", SearchOption.AllDirectories).Any());
            Assert.False(File.Exists(Path.Combine(root, "means.db")));

            await using var data = await store.GetObjectAsync("docs", "hello.txt", CancellationToken.None);
            using var reader = new StreamReader(data.Content, Encoding.UTF8);
            Assert.Equal(body, await reader.ReadToEndAsync());

            var list = await store.ListObjectsAsync("docs", new ListObjectsOptions(null, null, null, 100), CancellationToken.None);
            Assert.Single(list.Objects);
            Assert.Equal("hello.txt", list.Objects[0].Key);

            await store.DeleteObjectAsync("docs", "hello.txt", CancellationToken.None);
            await Assert.ThrowsAsync<MeansException>(() => store.HeadObjectAsync("docs", "hello.txt", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsPutObjectUsesReedSolomonShardsAndReadsThroughMissingDataShard()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            var payload = Enumerable.Range(0, 384 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "recoverable.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            Assert.Equal("reed-solomon-v1", manifest.Erasure.Algorithm);
            Assert.Equal(2, manifest.Erasure.DataShards);
            Assert.Equal(1, manifest.Erasure.ParityShards);
            Assert.Equal(3, manifest.Parts[0].Shards.Count);

            var firstDataShard = manifest.Parts[0].Shards.Single(shard => shard.SetIndex == 0);
            File.Delete(ResolveShardPath(root, firstDataShard));

            await using var data = await store.GetObjectAsync("ec", "recoverable.bin", CancellationToken.None);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsErasureCodingWritesAndReadsRemoteClusterShards()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 239))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "remote.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            Assert.Contains(manifest.Parts[0].Shards, shard => shard.DiskId == "remote-disk-00");
            Assert.Contains(manifest.Parts[0].Shards, shard => shard.DiskId == "remote-disk-01");
            Assert.Equal(2, transport.WrittenShardCount);
            Assert.Equal(2, transport.WrittenManifestCount);

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(0, consistency.MissingReplicaFileCount);
            Assert.Equal(0, consistency.UnderReplicatedObjectCount);

            var scrub = await store.ScrubObjectReplicasAsync(100, CancellationToken.None);
            Assert.Equal(3, scrub.CheckedReplicas);
            Assert.Equal(0, scrub.MissingReplicas);
            Assert.Equal(0, scrub.CorruptReplicas);
            Assert.True(transport.StatShardCount >= 2);

            await using var data = await store.GetObjectAsync("ec", "remote.bin", CancellationToken.None);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
            Assert.True(transport.ReadShardCount >= 1);

            await store.DeleteObjectAsync("ec", "remote.bin", CancellationToken.None);
            Assert.Equal(2, transport.DeletedShardCount);
            Assert.Equal(2, transport.DeletedManifestCount);
            await Assert.ThrowsAsync<MeansException>(() => store.GetObjectAsync("ec", "remote.bin", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsErasureCodingCommitsDegradedRemoteWriteWhenQuorumRemains()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        transport.FailShardWriteDiskIds.Add("remote-disk-01");
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 193))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "degraded-write.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            Assert.Equal(3, manifest.Parts[0].Shards.Count);
            var missingParityShard = manifest.Parts[0].Shards.Single(shard => shard.DiskId == "remote-disk-01");
            Assert.False(transport.HasShard(missingParityShard));
            Assert.Equal(1, transport.WrittenShardCount);

            await using (var data = await store.GetObjectAsync("ec", "degraded-write.bin", CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await data.Content.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(1, consistency.MissingReplicaFileCount);
            Assert.Equal(1, consistency.QueuedReplicaRepairCount);

            var diagnostics = await store.GetClusterDiagnosticsAsync(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), CancellationToken.None);
            Assert.Equal(1, diagnostics.ObjectReplicas.MissingReplicaFileCount);
            Assert.Equal(1, diagnostics.ObjectReplicas.DegradedObjectCount);
            Assert.Equal(1, diagnostics.ObjectReplicas.RecoverableDegradedObjectCount);
            Assert.Equal(0, diagnostics.ObjectReplicas.UnrecoverableObjectCount);
            Assert.Equal(0, diagnostics.ObjectReplicas.ReadQuorumLostObjectCount);
            Assert.Equal(0, diagnostics.ObjectReplicas.WriteQuorumLostObjectCount);

            transport.FailShardWriteDiskIds.Clear();
            var repaired = await store.RebuildErasureCodedObjectsAsync(100, CancellationToken.None);
            Assert.Equal(1, repaired);
            Assert.True(transport.HasShard(missingParityShard));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsErasureCodingRollsBackWhenRemoteWriteQuorumIsLost()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        transport.FailShardWriteDiskIds.Add("remote-disk-00");
        transport.FailShardWriteDiskIds.Add("remote-disk-01");
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 181))
                .ToArray();

            await Assert.ThrowsAsync<MeansException>(() => store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "quorum-lost.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None));

            Assert.Equal(0, transport.WrittenShardCount);
            Assert.Empty(Directory.EnumerateFiles(root, "xl.meta", SearchOption.AllDirectories));
            await Assert.ThrowsAsync<MeansException>(() => store.GetObjectAsync("ec", "quorum-lost.bin", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsErasureCodingCleansRemoteShardsWhenManifestReplicationFails()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport
        {
            FailManifestWrites = true
        };
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 211))
                .ToArray();

            await Assert.ThrowsAsync<MeansException>(() => store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "rollback.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None));

            Assert.Equal(2, transport.WrittenShardCount);
            Assert.Equal(2, transport.DeletedShardCount);
            Assert.Equal(0, transport.WrittenManifestCount);
            await Assert.ThrowsAsync<MeansException>(() => store.GetObjectAsync("ec", "rollback.bin", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsFullCopyWritesAndDeletesRemoteClusterReplicas()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport, erasureDataShards: 1, erasureParityShards: 0);
            await store.CreateBucketAsync("objects", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Encoding.UTF8.GetBytes("full-copy remote replica payload");

            await store.PutObjectAsync(new PutObjectRequest(
                "objects",
                "remote-full-copy.txt",
                new MemoryStream(payload),
                "text/plain",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            Assert.Equal("full-copy-v1", manifest.Erasure.Algorithm);
            Assert.Contains(manifest.Parts[0].Shards, shard => shard.DiskId == "remote-disk-00");
            Assert.Contains(manifest.Parts[0].Shards, shard => shard.DiskId == "remote-disk-01");
            Assert.Equal(2, transport.WrittenShardCount);
            Assert.Equal(2, transport.WrittenManifestCount);

            await using (var data = await store.GetObjectAsync("objects", "remote-full-copy.txt", CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await data.Content.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            await store.DeleteObjectAsync("objects", "remote-full-copy.txt", CancellationToken.None);
            Assert.Equal(2, transport.DeletedShardCount);
            Assert.Equal(2, transport.DeletedManifestCount);
            Assert.Equal(0, transport.StoredShardCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsFullCopyRepairRestoresMissingRemoteReplica()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport, erasureDataShards: 1, erasureParityShards: 0);
            await store.CreateBucketAsync("objects", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Encoding.UTF8.GetBytes("full-copy repair remote replica payload");

            await store.PutObjectAsync(new PutObjectRequest(
                "objects",
                "repair-full-copy.txt",
                new MemoryStream(payload),
                "text/plain",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            var remoteShard = manifest.Parts[0].Shards.Single(shard => shard.DiskId == "remote-disk-01");
            Assert.True(transport.RemoveShard(remoteShard));

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(1, consistency.MissingReplicaFileCount);
            Assert.Equal(1, consistency.QueuedReplicaRepairCount);

            var repaired = await store.RepairQueuedReplicasAsync(100, CancellationToken.None);
            Assert.Equal(1, repaired);
            Assert.True(transport.HasShard(remoteShard));
            Assert.True(transport.WrittenShardCount >= 3);

            var repairedConsistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(0, repairedConsistency.MissingReplicaFileCount);
            Assert.Equal(0, repairedConsistency.MissingReplicaManifestCount);

            await using var data = await store.GetObjectAsync("objects", "repair-full-copy.txt", CancellationToken.None);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartFullCopyCleansRemotePartReplicasOnAbort()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport, erasureDataShards: 1, erasureParityShards: 0);
            await store.CreateBucketAsync("media", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "remote-part.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);
            var payload = Enumerable.Range(0, 128 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();

            await store.UploadPartAsync(new UploadPartRequest(
                "media",
                "remote-part.bin",
                upload.UploadId,
                1,
                new MemoryStream(payload)), CancellationToken.None);

            Assert.Equal(2, transport.WrittenShardCount);
            Assert.Equal(2, transport.StoredShardCount);

            await store.AbortMultipartUploadAsync("media", "remote-part.bin", upload.UploadId, CancellationToken.None);

            Assert.Equal(2, transport.DeletedShardCount);
            Assert.Equal(0, transport.StoredShardCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsRepairQueueRebuildsMissingRemoteDataShard()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 229))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "repair-remote-data.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            var remoteDataShard = manifest.Parts[0].Shards.Single(shard => shard.SetIndex == 1);
            Assert.True(transport.RemoveShard(remoteDataShard));

            var scrub = await store.ScrubObjectReplicasAsync(100, CancellationToken.None);
            Assert.Equal(1, scrub.MissingReplicas);
            Assert.Equal(1, scrub.QueuedRepairs);

            var repaired = await store.RepairQueuedReplicasAsync(100, CancellationToken.None);
            Assert.Equal(1, repaired);
            Assert.True(transport.HasShard(remoteDataShard));
            Assert.True(transport.WrittenShardCount >= 3);

            await using var data = await store.GetObjectAsync("ec", "repair-remote-data.bin", CancellationToken.None);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsRebalanceMovesErasureShardToPlannedDisk()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new SwitchablePlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 227))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "rebalance.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var before = await ReadFirstXlManifestAsync(root);
            var oldParityShard = before.Parts[0].Shards.Single(shard => shard.SetIndex == 2);
            Assert.Equal("remote-disk-01", oldParityShard.DiskId);
            Assert.True(transport.HasShard(oldParityShard));

            planner.SetPlacements(
                ("local", "disk-00"),
                ("remote-a", "remote-disk-00"),
                ("local", "disk-01"));

            var migrated = await store.RebalanceObjectReplicasAsync(100, CancellationToken.None);
            Assert.Equal(1, migrated);

            var after = await ReadFirstXlManifestAsync(root);
            var rebalancedParityShard = after.Parts[0].Shards.Single(shard => shard.SetIndex == 2);
            Assert.Equal("disk-01", rebalancedParityShard.DiskId);
            Assert.True(File.Exists(ResolveShardPath(root, rebalancedParityShard)));
            Assert.False(transport.HasShard(oldParityShard));

            await using var data = await store.GetObjectAsync("ec", "rebalance.bin", CancellationToken.None);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(0, consistency.MissingReplicaFileCount);
            Assert.Equal(0, consistency.MissingReplicaManifestCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsRebalanceRespectsMaxItems()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new SwitchablePlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);

            for (var item = 0; item < 2; item++)
            {
                var payload = Enumerable.Range(0, 128 * 1024)
                    .Select(index => (byte)((index + item) % 251))
                    .ToArray();
                await store.PutObjectAsync(new PutObjectRequest(
                    "ec",
                    "rebalance-" + item + ".bin",
                    new MemoryStream(payload),
                    "application/octet-stream",
                    new Dictionary<string, string>(),
                    null,
                    null), CancellationToken.None);
            }

            planner.SetPlacements(
                ("local", "disk-00"),
                ("remote-a", "remote-disk-00"),
                ("local", "disk-01"));

            var migrated = await store.RebalanceObjectReplicasAsync(1, CancellationToken.None);
            Assert.Equal(1, migrated);

            var manifests = await ReadXlManifestsAsync(root);
            var rebalancedObjects = manifests
                .GroupBy(manifest => manifest.ObjectId, StringComparer.Ordinal)
                .Count(group => group.First().Parts[0].Shards.Any(shard => shard.SetIndex == 2 && shard.DiskId == "disk-01"));
            Assert.Equal(1, rebalancedObjects);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsConsistencyRepairRebuildsMissingRemoteParityShard()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 223))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "repair-remote-parity.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            var remoteParityShard = manifest.Parts[0].Shards.Single(shard => shard.SetIndex == 2);
            Assert.True(transport.RemoveShard(remoteParityShard));

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(1, consistency.MissingReplicaFileCount);
            Assert.Equal(1, consistency.QueuedReplicaRepairCount);

            var repaired = await store.RebuildErasureCodedObjectsAsync(100, CancellationToken.None);
            Assert.Equal(1, repaired);
            Assert.True(transport.HasShard(remoteParityShard));
            Assert.True(transport.WrittenShardCount >= 3);

            var scrub = await store.ScrubObjectReplicasAsync(100, CancellationToken.None);
            Assert.Equal(3, scrub.CheckedReplicas);
            Assert.Equal(0, scrub.MissingReplicas);
            Assert.Equal(0, scrub.CorruptReplicas);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsConsistencyRepairRewritesMissingRemoteManifestReplica()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 197))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "repair-remote-manifest.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            var remoteShard = manifest.Parts[0].Shards.Single(shard => shard.DiskId == "remote-disk-00");
            var manifestRelativePath = ManifestRelativePathFromShard(remoteShard);
            Assert.True(transport.RemoveManifest(remoteShard.DiskId, manifestRelativePath));

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(1, consistency.MissingReplicaManifestCount);
            Assert.Equal(0, consistency.MissingReplicaFileCount);
            Assert.Equal(1, consistency.QueuedReplicaRepairCount);
            Assert.True(transport.StatManifestCount >= 2);

            var repaired = await store.RepairQueuedReplicasAsync(100, CancellationToken.None);
            Assert.Equal(1, repaired);
            Assert.True(transport.HasManifest(remoteShard.DiskId, manifestRelativePath));
            Assert.True(transport.WrittenManifestCount >= 3);

            var repairedConsistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(0, repairedConsistency.MissingReplicaManifestCount);
            Assert.Equal(0, repairedConsistency.MissingReplicaFileCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsRepairQueueRecordsFailedRemoteRepairAfterMaxAttempts()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport, replicaRepairMaxAttempts: 1);
            await store.CreateBucketAsync("ec", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var payload = Enumerable.Range(0, 256 * 1024)
                .Select(index => (byte)(index % 193))
                .ToArray();

            await store.PutObjectAsync(new PutObjectRequest(
                "ec",
                "failed-repair.bin",
                new MemoryStream(payload),
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var manifest = await ReadFirstXlManifestAsync(root);
            var remoteDataShard = manifest.Parts[0].Shards.Single(shard => shard.SetIndex == 1);
            Assert.True(transport.RemoveShard(remoteDataShard));

            var consistency = await store.CheckMetadataConsistencyAsync(repair: true, 100, CancellationToken.None);
            Assert.Equal(1, consistency.QueuedReplicaRepairCount);

            transport.FailShardWrites = true;
            var repaired = await store.RepairQueuedReplicasAsync(100, CancellationToken.None);
            Assert.Equal(0, repaired);

            var diagnostics = await store.GetClusterDiagnosticsAsync(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), CancellationToken.None);
            Assert.Equal(1, diagnostics.RepairQueue.TotalCount);
            Assert.Equal(0, diagnostics.RepairQueue.PendingCount);
            Assert.Equal(1, diagnostics.RepairQueue.FailedCount);
            Assert.Equal(0, diagnostics.RepairQueue.RetryableFailedCount);
            Assert.Equal(1, diagnostics.RepairQueue.MaxAttemptsReachedCount);
            Assert.Contains(diagnostics.RepairQueue.Statuses, status => status.Status == "Failed" && status.Count == 1);
            var item = Assert.Single(diagnostics.RepairQueue.Items);
            Assert.Equal("ec", item.BucketName);
            Assert.Equal("failed-repair.bin", item.Key);
            Assert.Equal("ShardMissing", item.Reason);
            Assert.Equal("Failed", item.Status);
            Assert.Equal(1, item.AttemptCount);
            Assert.NotNull(item.LastError);
            Assert.NotNull(diagnostics.RepairQueue.LastUpdatedAt);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }


    [Fact]
    public async Task XlFsClusterShardStoreStreamsShardAndRejectsTraversal()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            var payload = Encoding.UTF8.GetBytes("remote shard payload");
            var relativePath = Path.Combine("objects", "bucket-hash", "object-id", "shard.00");
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

            var written = await store.WriteShardAsync(
                "disk-00",
                relativePath,
                new MemoryStream(payload),
                payload.Length,
                checksum,
                maxBytes: 1024 * 1024,
                CancellationToken.None);

            Assert.Equal(payload.Length, written.Length);
            Assert.Equal(checksum, written.ChecksumSha256);

            await using (var opened = await store.OpenShardAsync("disk-00", relativePath, CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await opened.Content.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            await Assert.ThrowsAsync<MeansException>(() => store.WriteShardAsync(
                "disk-00",
                Path.Combine("objects", "..", "escape"),
                new MemoryStream(payload),
                payload.Length,
                checksum,
                maxBytes: 1024 * 1024,
                CancellationToken.None));

            Assert.True(await store.DeleteShardAsync("disk-00", relativePath, CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartCompleteBuildsMultipartEtagAndCleansUpload()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("media", CancellationToken.None);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "movie.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var first = Enumerable.Repeat((byte)'a', 5 * 1024 * 1024).ToArray();
            var second = Encoding.UTF8.GetBytes("tail");
            var part1 = await store.UploadPartAsync(new UploadPartRequest("media", "movie.bin", upload.UploadId, 1, new MemoryStream(first)), CancellationToken.None);
            var part2 = await store.UploadPartAsync(new UploadPartRequest("media", "movie.bin", upload.UploadId, 2, new MemoryStream(second)), CancellationToken.None);

            var info = await store.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest(
                "media",
                "movie.bin",
                upload.UploadId,
                [new CompletedMultipartPart(1, part1.ETag), new CompletedMultipartPart(2, part2.ETag)]), CancellationToken.None);

            Assert.EndsWith("-2", info.ETag);
            Assert.Equal(first.Length + second.Length, info.ContentLength);
            await Assert.ThrowsAsync<MeansException>(() => store.ListPartsAsync("media", "movie.bin", upload.UploadId, 0, 1000, CancellationToken.None));

            await using var data = await store.GetObjectAsync("media", "movie.bin", CancellationToken.None);
            Assert.Equal(first.Length + second.Length, data.Info.ContentLength);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(first.Concat(second).ToArray(), buffer.ToArray());

            var manifestPath = Directory.EnumerateFiles(Path.Combine(root, "disk1", "objects"), "xl.meta", SearchOption.AllDirectories).Single();
            var manifest = JsonSerializer.Deserialize<XlObjectManifest>(await File.ReadAllTextAsync(manifestPath));
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest.Parts.Count);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartErasureCodingReadsMultiplePartsWithMissingDataShard()
    {
        var root = CreateTempRoot();
        var transport = new InMemoryClusterShardTransport();
        var planner = new FixedPlacementPlanner(
            ("local", "disk-00"),
            ("remote-a", "remote-disk-00"),
            ("remote-a", "remote-disk-01"));
        try
        {
            await using var store = CreateStore(root, planner, transport);
            await store.CreateBucketAsync("media", CancellationToken.None);
            await RegisterClusterTopologyAsync(store, root);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "ec-movie.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var first = Enumerable.Range(0, 5 * 1024 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            var second = Enumerable.Range(0, 257 * 1024)
                .Select(index => (byte)(255 - index % 251))
                .ToArray();
            var part1 = await store.UploadPartAsync(new UploadPartRequest("media", "ec-movie.bin", upload.UploadId, 1, new MemoryStream(first)), CancellationToken.None);
            var part2 = await store.UploadPartAsync(new UploadPartRequest("media", "ec-movie.bin", upload.UploadId, 2, new MemoryStream(second)), CancellationToken.None);

            var info = await store.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest(
                "media",
                "ec-movie.bin",
                upload.UploadId,
                [new CompletedMultipartPart(1, part1.ETag), new CompletedMultipartPart(2, part2.ETag)]), CancellationToken.None);

            Assert.EndsWith("-2", info.ETag);
            Assert.Equal(first.Length + second.Length, info.ContentLength);
            var manifest = await ReadFirstXlManifestAsync(root);
            Assert.Equal("reed-solomon-v1", manifest.Erasure.Algorithm);
            Assert.Equal(2, manifest.Parts.Count);
            Assert.All(manifest.Parts, part => Assert.Equal(3, part.Shards.Count));
            Assert.Equal(4, transport.WrittenShardCount);

            var firstDataShard = manifest.Parts[0].Shards.Single(shard => shard.SetIndex == 0);
            File.Delete(ResolveShardPath(root, firstDataShard));

            await using (var data = await store.GetObjectAsync("media", "ec-movie.bin", CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await data.Content.CopyToAsync(buffer);
                Assert.Equal(first.Concat(second).ToArray(), buffer.ToArray());
            }

            var diagnostics = await store.GetClusterDiagnosticsAsync(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), CancellationToken.None);
            Assert.Equal(1, diagnostics.ObjectReplicas.DegradedObjectCount);
            Assert.Equal(1, diagnostics.ObjectReplicas.RecoverableDegradedObjectCount);
            Assert.Equal(0, diagnostics.ObjectReplicas.UnrecoverableObjectCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartRejectsInvalidPartOrder()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("media", CancellationToken.None);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "bad.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var ex = await Assert.ThrowsAsync<MeansException>(() => store.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest(
                    "media",
                    "bad.bin",
                    upload.UploadId,
                    [new CompletedMultipartPart(2, "etag"), new CompletedMultipartPart(1, "etag")]),
                CancellationToken.None));

            Assert.Equal(MeansErrorCodes.InvalidPartOrder, ex.Code);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static XlFsStore CreateStore(
        string root,
        IObjectPlacementPlanner? placementPlanner = null,
        IClusterShardTransport? shardTransport = null,
        int replicaRepairMaxAttempts = 5,
        int erasureDataShards = 2,
        int erasureParityShards = 1)
    {
        var options = Options.Create(new XlFsOptions
        {
            ObjectsPath = Path.Combine(root, "objects"),
            Disks =
            [
                Path.Combine(root, "disk1"),
                Path.Combine(root, "disk2"),
                Path.Combine(root, "disk3")
            ],
            DeploymentId = "unit-test",
            SetId = "set-1",
            ErasureDataShards = erasureDataShards,
            ErasureParityShards = erasureParityShards,
            WriteQuorum = 2,
            ReadQuorum = 1,
            DefaultAccessKey = "meansadmin",
            DefaultSecretKey = "meansadminsecret",
            ReplicaRepairMaxAttempts = replicaRepairMaxAttempts
        });
        return placementPlanner is null && shardTransport is null
            ? new XlFsStore(options)
            : new XlFsStore(
                options,
                placementPlanner ?? new DeterministicObjectPlacementPlanner(),
                shardTransport ?? new DisabledClusterShardTransport());
    }

    private static async Task RegisterClusterTopologyAsync(XlFsStore store, string root)
    {
        var now = DateTimeOffset.UtcNow;
        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "cluster-a",
                "Cluster A",
                "local",
                "local",
                "http://local",
                "pool-a",
                "Pool A",
                [
                    DiskRegistration("disk-00", Path.Combine(root, "disk1")),
                    DiskRegistration("disk-01", Path.Combine(root, "disk2")),
                    DiskRegistration("disk-02", Path.Combine(root, "disk3"))
                ],
                now),
            CancellationToken.None);
        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "cluster-a",
                "Cluster A",
                "remote-a",
                "remote-a",
                "http://remote-a",
                "pool-a",
                "Pool A",
                [
                    DiskRegistration("remote-disk-00", "/remote/disk0"),
                    DiskRegistration("remote-disk-01", "/remote/disk1")
                ],
                now),
            CancellationToken.None);
    }

    private static StorageDiskRegistration DiskRegistration(string diskId, string path)
    {
        return new StorageDiskRegistration(
            diskId,
            "pool-a",
            path,
            10L * 1024 * 1024 * 1024,
            9L * 1024 * 1024 * 1024,
            StorageDiskStatuses.Online);
    }

    private static async Task<XlObjectManifest> ReadFirstXlManifestAsync(string root)
    {
        var manifestPath = Directory.EnumerateFiles(root, "xl.meta", SearchOption.AllDirectories).First();
        return JsonSerializer.Deserialize<XlObjectManifest>(await File.ReadAllTextAsync(manifestPath))
            ?? throw new InvalidOperationException("Test manifest could not be parsed.");
    }

    private static async Task<IReadOnlyList<XlObjectManifest>> ReadXlManifestsAsync(string root)
    {
        var manifests = new List<XlObjectManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "xl.meta", SearchOption.AllDirectories))
        {
            manifests.Add(JsonSerializer.Deserialize<XlObjectManifest>(await File.ReadAllTextAsync(manifestPath))
                ?? throw new InvalidOperationException("Test manifest could not be parsed."));
        }

        return manifests;
    }

    private static string ResolveShardPath(string root, XlShardManifest shard)
    {
        foreach (var diskRoot in Directory.EnumerateDirectories(root, "disk*"))
        {
            var path = Path.Combine(diskRoot, shard.RelativePath);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Could not resolve shard file for test.", shard.RelativePath);
    }

    private static string ManifestRelativePathFromShard(XlShardManifest shard)
    {
        return Path.Combine(Path.GetDirectoryName(shard.RelativePath)!, "xl.meta");
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), "means-xlfs-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class FixedPlacementPlanner(params (string NodeId, string DiskId)[] placements) : IObjectPlacementPlanner
    {
        public ObjectPlacementPlan PlanPlacement(ObjectPlacementRequest request, ClusterTopology topology)
        {
            var disks = topology.Nodes
                .SelectMany(node => node.Disks.Select(disk => (node.NodeId, Disk: disk)))
                .ToDictionary(item => (item.NodeId, item.Disk.DiskId), item => item.Disk);
            return new ObjectPlacementPlan(
                request.BucketName,
                request.ObjectKey,
                request.VersionId,
                placements.Take(request.ReplicaCount)
                    .Select((placement, index) =>
                    {
                        var disk = disks[(placement.NodeId, placement.DiskId)];
                        return new ObjectPlacementReplica(
                            index,
                            placement.NodeId,
                            placement.DiskId,
                            disk.PoolId,
                            disk.MountPath);
                    })
                    .ToArray());
        }
    }

    private sealed class SwitchablePlacementPlanner(params (string NodeId, string DiskId)[] initialPlacements) : IObjectPlacementPlanner
    {
        private (string NodeId, string DiskId)[] _placements = initialPlacements;

        public void SetPlacements(params (string NodeId, string DiskId)[] placements)
        {
            _placements = placements;
        }

        public ObjectPlacementPlan PlanPlacement(ObjectPlacementRequest request, ClusterTopology topology)
        {
            var disks = topology.Nodes
                .SelectMany(node => node.Disks.Select(disk => (node.NodeId, Disk: disk)))
                .ToDictionary(item => (item.NodeId, item.Disk.DiskId), item => item.Disk);
            return new ObjectPlacementPlan(
                request.BucketName,
                request.ObjectKey,
                request.VersionId,
                _placements.Take(request.ReplicaCount)
                    .Select((placement, index) =>
                    {
                        var disk = disks[(placement.NodeId, placement.DiskId)];
                        return new ObjectPlacementReplica(
                            index,
                            placement.NodeId,
                            placement.DiskId,
                            disk.PoolId,
                            disk.MountPath);
                    })
                    .ToArray());
        }
    }

    private sealed class InMemoryClusterShardTransport : IClusterShardTransport
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _shards = new(StringComparer.Ordinal);
        private int _deletedManifestCount;
        private int _deletedShardCount;
        private int _writtenManifestCount;
        private int _readShardCount;
        private int _statManifestCount;
        private int _statShardCount;
        private int _writtenShardCount;

        public bool Enabled => true;

        public bool FailManifestWrites { get; init; }

        public bool FailShardWrites { get; set; }

        public HashSet<string> FailShardWriteDiskIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailManifestWriteDiskIds { get; } = new(StringComparer.Ordinal);

        public int WrittenShardCount => _writtenShardCount;

        public int WrittenManifestCount => _writtenManifestCount;

        public int ReadShardCount => _readShardCount;

        public int StatShardCount => _statShardCount;

        public int StatManifestCount => _statManifestCount;

        public int DeletedShardCount => _deletedShardCount;

        public int DeletedManifestCount => _deletedManifestCount;

        public int StoredShardCount
        {
            get
            {
                lock (_gate)
                {
                    return _shards.Count;
                }
            }
        }

        public bool RemoveShard(XlShardManifest shard)
        {
            lock (_gate)
            {
                return _shards.Remove(Key(shard.DiskId, shard.RelativePath));
            }
        }

        public bool HasShard(XlShardManifest shard)
        {
            lock (_gate)
            {
                return _shards.ContainsKey(Key(shard.DiskId, shard.RelativePath));
            }
        }

        public bool RemoveManifest(string diskId, string relativePath)
        {
            lock (_gate)
            {
                return _manifests.Remove(Key(diskId, relativePath));
            }
        }

        public bool HasManifest(string diskId, string relativePath)
        {
            lock (_gate)
            {
                return _manifests.ContainsKey(Key(diskId, relativePath));
            }
        }

        public async Task<ClusterShardWriteResult> WriteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            if (FailShardWrites || FailShardWriteDiskIds.Contains(diskId))
            {
                throw new MeansException(MeansErrorCodes.SlowDown, "Simulated shard replication failure.", 503);
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expectedLength, bytes.Length);
            Assert.Equal(expectedChecksumSha256, checksum);
            lock (_gate)
            {
                _shards[Key(diskId, relativePath)] = bytes;
                _writtenShardCount++;
            }

            return new ClusterShardWriteResult(diskId, relativePath, bytes.Length, checksum);
        }

        public Task<ClusterShardReadResult> OpenShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            lock (_gate)
            {
                if (!_shards.TryGetValue(Key(diskId, relativePath), out bytes!))
                {
                    throw new MeansException(MeansErrorCodes.NoSuchKey, "Shard not found.", 404);
                }

                _readShardCount++;
            }

            return Task.FromResult(new ClusterShardReadResult(
                diskId,
                relativePath,
                bytes.Length,
                new MemoryStream(bytes, writable: false)));
        }

        public Task<bool> DeleteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var deleted = _shards.Remove(Key(diskId, relativePath));
                if (deleted)
                {
                    _deletedShardCount++;
                }

                return Task.FromResult(deleted);
            }
        }

        public Task<ClusterShardStatResult> StatShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            lock (_gate)
            {
                if (!_shards.TryGetValue(Key(diskId, relativePath), out bytes!))
                {
                    throw new MeansException(MeansErrorCodes.NoSuchKey, "Shard not found.", 404);
                }

                _statShardCount++;
            }

            return Task.FromResult(new ClusterShardStatResult(
                diskId,
                relativePath,
                bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        private static string Key(string diskId, string relativePath)
        {
            return diskId + "/" + relativePath.Replace('\\', '/');
        }

        public async Task<ClusterShardWriteResult> WriteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            if (FailManifestWrites || FailManifestWriteDiskIds.Contains(diskId))
            {
                throw new MeansException(MeansErrorCodes.SlowDown, "Simulated manifest replication failure.", 503);
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expectedLength, bytes.Length);
            Assert.Equal(expectedChecksumSha256, checksum);
            lock (_gate)
            {
                _manifests[Key(diskId, relativePath)] = bytes;
                _writtenManifestCount++;
            }

            return new ClusterShardWriteResult(diskId, relativePath, bytes.Length, checksum);
        }

        public Task<ClusterShardReadResult> OpenManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            lock (_gate)
            {
                if (!_manifests.TryGetValue(Key(diskId, relativePath), out bytes!))
                {
                    throw new MeansException(MeansErrorCodes.NoSuchKey, "Manifest not found.", 404);
                }
            }

            return Task.FromResult(new ClusterShardReadResult(
                diskId,
                relativePath,
                bytes.Length,
                new MemoryStream(bytes, writable: false)));
        }

        public Task<bool> DeleteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var deleted = _manifests.Remove(Key(diskId, relativePath));
                if (deleted)
                {
                    _deletedManifestCount++;
                }

                return Task.FromResult(deleted);
            }
        }

        public Task<ClusterShardStatResult> StatManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            lock (_gate)
            {
                _statManifestCount++;
                if (!_manifests.TryGetValue(Key(diskId, relativePath), out bytes!))
                {
                    throw new MeansException(MeansErrorCodes.NoSuchKey, "Manifest not found.", 404);
                }
            }

            return Task.FromResult(new ClusterShardStatResult(
                diskId,
                relativePath,
                bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
        }
    }

    private sealed class DisabledClusterShardTransport : IClusterShardTransport
    {
        public bool Enabled => false;

        public Task<ClusterShardWriteResult> WriteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ClusterShardReadResult> OpenShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ClusterShardStatResult> StatShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ClusterShardWriteResult> WriteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ClusterShardReadResult> OpenManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ClusterShardStatResult> StatManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
