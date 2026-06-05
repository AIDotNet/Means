using System.Security.Cryptography;
using System.Text;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task RegisterNodeAsync(ClusterNodeRegistration registration, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await EnsureInitializedAsync(cancellationToken);
        var registeredAt = registration.RegisteredAt.ToUniversalTime();
        var existingCluster = await Db.GetJsonAsync<StorageClusterInfo>(Keys.ClusterInfo, cancellationToken);
        var cluster = new StorageClusterInfo(
            registration.ClusterId,
            registration.ClusterName,
            existingCluster is not null && string.Equals(existingCluster.ClusterId, registration.ClusterId, StringComparison.Ordinal)
                ? existingCluster.CreatedAt
                : registeredAt);
        var existingNode = await Db.GetJsonAsync<ClusterNodeInfo>(Keys.ClusterNode(registration.NodeId), cancellationToken);
        var incomingDisks = registration.Disks.Select(disk => new StorageDiskInfo(
            disk.DiskId,
            registration.NodeId,
            disk.PoolId,
            disk.MountPath,
            Math.Max(0, disk.TotalBytes),
            Math.Max(0, disk.AvailableBytes),
            NormalizeDiskStatus(disk.Status),
            registeredAt)).ToArray();
        var incomingDiskIds = incomingDisks.Select(disk => disk.DiskId).ToHashSet(StringComparer.Ordinal);
        var disks = existingNode is not null && string.Equals(existingNode.ClusterId, registration.ClusterId, StringComparison.Ordinal)
            ? incomingDisks.Concat(existingNode.Disks
                .Where(disk => !incomingDiskIds.Contains(disk.DiskId))
                .Select(disk => disk with { Status = StorageDiskStatuses.Offline, LastSeenAt = registeredAt }))
                .ToArray()
            : incomingDisks;
        var node = new ClusterNodeInfo(
            registration.NodeId,
            registration.ClusterId,
            registration.HostName,
            registration.Endpoint,
            ClusterNodeStatuses.Online,
            existingNode is not null && string.Equals(existingNode.ClusterId, registration.ClusterId, StringComparison.Ordinal)
                ? existingNode.RegisteredAt
                : registeredAt,
            registeredAt,
            disks,
            NormalizeFaultDomain(registration.FaultDomain, registration.NodeId));
        var existingPool = await Db.GetJsonAsync<XlStoragePoolRecord>(Keys.ClusterPool(registration.ClusterId, registration.PoolId), cancellationToken);
        var pool = new XlStoragePoolRecord(
            registration.PoolId,
            registration.ClusterId,
            registration.PoolName,
            existingPool?.CreatedAt ?? registeredAt,
            registeredAt);

        await Db.PutBatchAsync([
            new LogDbMutation(Keys.ClusterInfo, Serialize(cluster), false),
            new LogDbMutation(Keys.ClusterPool(registration.ClusterId, registration.PoolId), Serialize(pool), false),
            new LogDbMutation(Keys.ClusterNode(node.NodeId), Serialize(node), false)
        ], cancellationToken);
    }

    public async Task HeartbeatNodeAsync(ClusterNodeHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(heartbeat.NodeId))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Node id is required.", 400);
        }

        await EnsureInitializedAsync(cancellationToken);
        var node = await Db.GetJsonAsync<ClusterNodeInfo>(Keys.ClusterNode(heartbeat.NodeId), cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.InvalidArgument, "Node is not registered.", 404);
        var disksById = heartbeat.Disks.ToDictionary(disk => disk.DiskId, StringComparer.Ordinal);
        var disks = node.Disks.Select(disk =>
        {
            if (!disksById.TryGetValue(disk.DiskId, out var heartbeatDisk))
            {
                return disk with { Status = StorageDiskStatuses.Offline };
            }

            return disk with
            {
                TotalBytes = Math.Max(0, heartbeatDisk.TotalBytes),
                AvailableBytes = Math.Max(0, heartbeatDisk.AvailableBytes),
                Status = NormalizeDiskStatus(heartbeatDisk.Status),
                LastSeenAt = heartbeatDisk.LastSeenAt.ToUniversalTime()
            };
        }).ToArray();
        var updated = node with
        {
            Status = ClusterNodeStatuses.Online,
            LastHeartbeatAt = heartbeat.HeartbeatAt.ToUniversalTime(),
            Disks = disks
        };
        await Db.PutJsonAsync(Keys.ClusterNode(updated.NodeId), updated, cancellationToken);
    }

    public async Task<ClusterTopology> GetClusterTopologyAsync(
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        RefreshLocalDiskCapacity();
        var cluster = await Db.GetJsonAsync<StorageClusterInfo>(Keys.ClusterInfo, cancellationToken);
        var nodeRows = await Db.ScanPrefixAsync(Keys.ClusterNodePrefix, 100_000, null, cancellationToken);
        var nodes = nodeRows.Select(row => Deserialize<ClusterNodeInfo>(row.Value))
            .Where(node => cluster is null || string.Equals(node.ClusterId, cluster.ClusterId, StringComparison.Ordinal))
            .Select(node => node.LastHeartbeatAt < offlineBeforeUtc.ToUniversalTime()
                ? node with
                {
                    Status = ClusterNodeStatuses.Offline,
                    Disks = node.Disks.Select(disk => disk with { Status = StorageDiskStatuses.Offline }).ToArray()
                }
                : node)
            .ToArray();

        if (cluster is null || nodes.Length == 0)
        {
            return BuildLocalTopology(offlineBeforeUtc);
        }

        var poolRows = await Db.ScanPrefixAsync(Keys.ClusterPoolPrefix(cluster.ClusterId), 100_000, null, cancellationToken);
        var poolNames = poolRows.Select(row => Deserialize<XlStoragePoolRecord>(row.Value))
            .ToDictionary(pool => pool.PoolId, pool => pool.Name, StringComparer.Ordinal);
        var pools = nodes.SelectMany(node => node.Disks)
            .GroupBy(disk => disk.PoolId, StringComparer.Ordinal)
            .Select(group =>
            {
                var online = group.Where(disk => string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal)).ToArray();
                return new StoragePoolInfo(
                    group.Key,
                    cluster.ClusterId,
                    poolNames.TryGetValue(group.Key, out var name) ? name : group.Key,
                    cluster.CreatedAt,
                    group.Select(disk => disk.NodeId).Distinct(StringComparer.Ordinal).Count(),
                    group.Count(),
                    online.Sum(disk => disk.TotalBytes),
                    online.Sum(disk => disk.AvailableBytes));
            })
            .OrderBy(pool => pool.PoolId, StringComparer.Ordinal)
            .ToArray();
        return new ClusterTopology(cluster, nodes, pools);
    }

    public async Task<IReadOnlyList<ErasureCodingProfile>> ListErasureCodingProfilesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.EcProfilePrefix, 100_000, null, cancellationToken);
        var profiles = rows.Select(row => Deserialize<ErasureCodingProfile>(row.Value)).ToList();
        if (profiles.Count == 0)
        {
            profiles.Add(DefaultEcProfile());
        }

        return profiles.OrderBy(profile => profile.ProfileId, StringComparer.Ordinal).ToArray();
    }

    public async Task<ErasureCodingProfile?> GetErasureCodingProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalized = NormalizeErasureCodingProfileId(profileId);
        if (normalized == "xlfs-default")
        {
            return DefaultEcProfile();
        }

        return await Db.GetJsonAsync<ErasureCodingProfile>(Keys.EcProfile(normalized), cancellationToken);
    }

    public async Task<ErasureCodingProfile> SaveErasureCodingProfileAsync(
        ErasureCodingProfile profile,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var normalized = ValidateErasureCodingProfile(profile);
        var existing = await Db.GetJsonAsync<ErasureCodingProfile>(Keys.EcProfile(normalized.ProfileId), cancellationToken);
        var saved = normalized with
        {
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
        await Db.PutJsonAsync(Keys.EcProfile(saved.ProfileId), saved, cancellationToken);
        return saved;
    }

    public async Task DeleteErasureCodingProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalized = NormalizeErasureCodingProfileId(profileId);
        if (normalized == "xlfs-default")
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "The default XlFs EC profile cannot be deleted.", 400);
        }

        await Db.PutBatchAsync([new LogDbMutation(Keys.EcProfile(normalized), null, true)], cancellationToken);
    }

    public Task<IReadOnlyList<SchemaMigrationInfo>> ListSchemaMigrationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SchemaMigrationInfo>>([
            new SchemaMigrationInfo("xlfs-format-v1", DateTimeOffset.UnixEpoch)
        ]);
    }

    public async Task<MetadataSnapshotInfo> CreateMetadataSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot path is required.", 400);
        }

        await EnsureInitializedAsync(cancellationToken);
        return await Db.CreateSnapshotAsync(ResolvePath(snapshotPath), cancellationToken);
    }

    public async Task<MetadataSnapshotInfo> RestoreMetadataSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot path is required.", 400);
        }

        var resolved = ResolvePath(snapshotPath);
        if (!File.Exists(resolved))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot file does not exist.", 404);
        }

        await EnsureInitializedAsync(cancellationToken);
        await Db.RestoreSnapshotAsync(resolved, cancellationToken);
        var file = new FileInfo(resolved);
        return new MetadataSnapshotInfo(resolved, file.Length, DateTimeOffset.UtcNow);
    }

    public async Task<MetadataConsistencyCheckResult> CheckMetadataConsistencyAsync(
        bool repair,
        int maxItems,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var limit = Math.Clamp(maxItems, 1, 100_000);
        var rows = await Db.ScanPrefixAsync(Keys.CurrentObjectGlobalPrefix, limit, null, cancellationToken);
        long checkedCount = 0;
        long missingManifest = 0;
        long underReplicated = 0;
        long missingFiles = 0;
        long queued = 0;

        foreach (var row in rows)
        {
            var record = Deserialize<XlObjectRecord>(row.Value);
            if (record.IsDeleteMarker)
            {
                continue;
            }

            checkedCount++;
            var manifest = await TryReadManifestAsync(record, cancellationToken);
            if (manifest is null)
            {
                missingManifest++;
                if (repair)
                {
                    await QueueHealAsync(ToObjectInfo(record), "ManifestMissing", cancellationToken);
                    queued++;
                }

                continue;
            }

            var missingManifestReplicas = await CountUnavailableManifestReplicasAsync(manifest, cancellationToken);
            missingManifest += missingManifestReplicas;
            var queuedObjectRepair = false;
            if (repair && missingManifestReplicas > 0)
            {
                await QueueHealAsync(ToObjectInfo(record), "ManifestReplicaMissing", cancellationToken);
                queued++;
                queuedObjectRepair = true;
            }

            var existingShards = 0;
            var missingObjectShards = 0;
            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                var probe = await ProbeShardAsync(shard, verifyChecksum: false, cancellationToken);
                if (probe.Status != ShardProbeStatus.Available)
                {
                    missingFiles++;
                    missingObjectShards++;
                    continue;
                }

                existingShards++;
            }

            if (repair && missingObjectShards > 0 && !queuedObjectRepair)
            {
                await QueueHealAsync(ToObjectInfo(record), "ShardMissing", cancellationToken);
                queued++;
                queuedObjectRepair = true;
            }

            if (existingShards < WriteQuorum)
            {
                underReplicated++;
                if (repair && !queuedObjectRepair)
                {
                    await QueueHealAsync(ToObjectInfo(record), "UnderReplicated", cancellationToken);
                    queued++;
                }
            }
        }

        return new MetadataConsistencyCheckResult(
            checkedCount,
            0,
            0,
            missingManifest,
            underReplicated,
            missingFiles,
            queued,
            0,
            0);
    }

    public async Task<StorageGarbageCollectionResult> CollectStorageGarbageAsync(
        bool delete,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var limit = Math.Clamp(maxFiles, 1, 100_000);
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(Math.Max(1, _options.GarbageCollectionTempFileAgeMinutes)));
        var referenced = await ReadReferencedStoragePathsAsync(cancellationToken);
        long scanned = 0;
        long candidates = 0;
        long deleted = 0;
        long failed = 0;
        long orphanedReplicaFiles = 0;
        long orphanedMultipartFiles = 0;
        long expiredTempFiles = 0;
        var limitReached = false;

        foreach (var disk in _disks)
        {
            ScanPath(Path.Combine(disk.RootPath, "objects"), isTempRoot: false);
            ScanPath(Path.Combine(disk.RootPath, ".means.sys", "tmp"), isTempRoot: true);
            if (limitReached)
            {
                break;
            }
        }

        return new StorageGarbageCollectionResult(
            delete,
            limitReached,
            scanned,
            candidates,
            deleted,
            failed,
            orphanedReplicaFiles,
            0,
            orphanedMultipartFiles,
            0,
            expiredTempFiles);

        void ScanPath(string path, bool isTempRoot)
        {
            foreach (var file in EnumerateFilesSafe(path, "*", SearchOption.AllDirectories))
            {
                if (limitReached)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                var full = NormalizeStoragePath(file);
                var isTemp = isTempRoot || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (!isTemp && referenced.Contains(full))
                {
                    continue;
                }

                if (!IsOlderThan(full, cutoff))
                {
                    continue;
                }

                candidates++;
                if (isTemp)
                {
                    expiredTempFiles++;
                }
                else if (full.Contains($"{Path.DirectorySeparatorChar}multipart-", StringComparison.Ordinal))
                {
                    orphanedMultipartFiles++;
                }
                else
                {
                    orphanedReplicaFiles++;
                }

                if (delete)
                {
                    try
                    {
                        File.Delete(full);
                        deleted++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                if (candidates >= limit)
                {
                    limitReached = true;
                }
            }
        }
    }

    public async Task<int> DetectAndIsolateFailedDisksAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _disks.Count(disk =>
            !Directory.Exists(disk.RootPath)
            || !File.Exists(Path.Combine(disk.RootPath, ".means.sys", "format.json")));
    }

    public async Task<int> EnqueueMissingReplicaRepairsAsync(CancellationToken cancellationToken)
    {
        var result = await CheckMetadataConsistencyAsync(repair: true, _options.ScannerBatchSize, cancellationToken);
        return Convert.ToInt32(Math.Min(int.MaxValue, result.QueuedReplicaRepairCount));
    }

    public async Task<int> RepairQueuedReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var batchLimit = Math.Clamp(maxItems, 1, 10_000);
        var scanLimit = Math.Clamp(batchLimit * 4, batchLimit, 10_000);
        var rows = await Db.ScanPrefixAsync(Keys.HealPrefix, scanLimit, null, cancellationToken);
        var candidates = new List<(string RowKey, XlHealRecord Repair)>(batchLimit);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var repair = DeserializeHealRecord(row.Value, now);
            if (string.IsNullOrWhiteSpace(repair.BucketName)
                || string.IsNullOrWhiteSpace(repair.Key)
                || string.IsNullOrWhiteSpace(repair.ObjectId))
            {
                await Db.PutBatchAsync([new LogDbMutation(row.Key, null, true)], cancellationToken);
                continue;
            }

            if (repair.AttemptCount >= MaxRepairAttempts)
            {
                if (!string.Equals(repair.Status, HealStatuses.Failed, StringComparison.Ordinal))
                {
                    await Db.PutJsonAsync(row.Key, repair with
                    {
                        Status = HealStatuses.Failed,
                        UpdatedAt = now,
                        NextAttemptAt = null
                    }, cancellationToken);
                }

                continue;
            }

            if (repair.NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now)
            {
                continue;
            }

            if (candidates.Count >= batchLimit)
            {
                break;
            }

            candidates.Add((row.Key, repair));
        }

        var repaired = 0;
        var throttleDelay = RepairThrottleDelay;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxRepairConcurrency
            },
            async (candidate, token) =>
            {
                if (throttleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(throttleDelay, token);
                }

                if (await TryRepairQueuedReplicaAsync(candidate.RowKey, candidate.Repair, token))
                {
                    Interlocked.Increment(ref repaired);
                }
            });

        return repaired;
    }

    private async Task<bool> TryRepairQueuedReplicaAsync(
        string rowKey,
        XlHealRecord repair,
        CancellationToken cancellationToken)
    {
        var attemptStartedAt = DateTimeOffset.UtcNow;
        var attempting = repair with
        {
            Status = HealStatuses.Pending,
            LastAttemptAt = attemptStartedAt,
            UpdatedAt = attemptStartedAt,
            NextAttemptAt = null
        };
        await Db.PutJsonAsync(rowKey, attempting, cancellationToken);
        try
        {
            var record = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(repair.BucketName, repair.Key), cancellationToken);
            if (record is null || !string.Equals(record.ObjectId, repair.ObjectId, StringComparison.Ordinal))
            {
                await Db.PutBatchAsync([new LogDbMutation(rowKey, null, true)], cancellationToken);
                return false;
            }

            if (!await RepairObjectShardsAsync(record, cancellationToken))
            {
                throw new MeansException(MeansErrorCodes.SlowDown, "Object repair could not be completed from available shards.", 503);
            }

            await Db.PutBatchAsync([new LogDbMutation(rowKey, null, true)], cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attemptCount = attempting.AttemptCount + 1;
            var maxAttemptsReached = attemptCount >= MaxRepairAttempts;
            var failed = attempting with
            {
                Status = maxAttemptsReached ? HealStatuses.Failed : HealStatuses.RetryScheduled,
                AttemptCount = attemptCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastAttemptAt = attemptStartedAt,
                NextAttemptAt = maxAttemptsReached ? null : DateTimeOffset.UtcNow.Add(RepairRetryDelay(attemptCount)),
                LastError = TruncateHealError(ex.Message)
            };
            await Db.PutJsonAsync(rowKey, failed, cancellationToken);
            return false;
        }
    }

    public Task<int> RebuildErasureCodedObjectsAsync(int maxItems, CancellationToken cancellationToken)
    {
        return RepairQueuedReplicasAsync(maxItems, cancellationToken);
    }

    private static TimeSpan RepairRetryDelay(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 6);
        return TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << exponent)));
    }

    public async Task<int> RebalanceObjectReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var batchLimit = Math.Clamp(maxItems, 1, 10_000);
        var topology = await GetClusterTopologyAsync(
            DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(60)),
            cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.CurrentObjectGlobalPrefix, batchLimit, null, cancellationToken);
        var migrated = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = Deserialize<XlObjectRecord>(row.Value);
            if (record.IsDeleteMarker)
            {
                continue;
            }

            try
            {
                var manifest = await TryReadManifestAsync(record, cancellationToken);
                if (manifest is null || manifest.Parts.Count == 0)
                {
                    continue;
                }

                var rebalanced = IsReedSolomonErasure(manifest)
                    ? await TryRebalanceReedSolomonObjectAsync(record, manifest, topology, cancellationToken)
                    : await TryRebalanceFullCopyObjectAsync(record, manifest, topology, cancellationToken);
                if (rebalanced)
                {
                    migrated++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return migrated;
    }

    private async Task<bool> TryRebalanceReedSolomonObjectAsync(
        XlObjectRecord record,
        XlObjectManifest manifest,
        ClusterTopology topology,
        CancellationToken cancellationToken)
    {
        var totalShards = manifest.Erasure.DataShards + manifest.Erasure.ParityShards;
        if (totalShards <= 0 || manifest.Parts.Any(part => part.Shards.Count == 0))
        {
            return false;
        }

        var plannedTargets = TryPlanRebalanceTargets(
            record.BucketName,
            record.ObjectId,
            record.ObjectId,
            totalShards,
            manifest.Parts.Max(part => part.Shards.Max(shard => shard.Size)),
            topology);
        if (plannedTargets is null || plannedTargets.Count != totalShards)
        {
            return false;
        }

        var oldLocations = manifest.Parts
            .SelectMany(part => part.Shards)
            .Select(ShardLocationKey)
            .ToHashSet(StringComparer.Ordinal);
        var stagedNewShards = new List<XlShardManifest>();
        var wroteShard = false;
        var updatedParts = new List<XlPartManifest>(manifest.Parts.Count);
        try
        {
            foreach (var part in manifest.Parts)
            {
                var existingByIndex = part.Shards
                    .GroupBy(shard => shard.SetIndex)
                    .ToDictionary(group => group.Key, group => group.First());
                var updatedShards = new List<XlShardManifest>(totalShards);
                for (var shardIndex = 0; shardIndex < totalShards; shardIndex++)
                {
                    if (!existingByIndex.TryGetValue(shardIndex, out var sourceShard))
                    {
                        await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                        return false;
                    }

                    var target = plannedTargets[shardIndex];
                    var targetShard = new XlShardManifest(
                        target.DiskId,
                        shardIndex,
                        ObjectShardRelativePath(record.BucketName, record.ObjectId, shardIndex),
                        sourceShard.Size,
                        sourceShard.ChecksumSha256);
                    var targetProbe = await ProbeShardAsync(targetShard, verifyChecksum: true, cancellationToken);
                    if (targetProbe.Status == ShardProbeStatus.Available)
                    {
                        updatedShards.Add(targetShard);
                        continue;
                    }

                    var copied = false;
                    var sourceProbe = await ProbeShardAsync(sourceShard, verifyChecksum: true, cancellationToken);
                    if (sourceProbe.Status == ShardProbeStatus.Available)
                    {
                        copied = await TryCopyShardToTargetAsync(sourceShard, targetShard, cancellationToken);
                    }

                    if (!copied
                        && !await TryRepairReedSolomonShardAsync(manifest.Erasure, part, targetShard, cancellationToken))
                    {
                        await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                        return false;
                    }

                    wroteShard = true;
                    if (!oldLocations.Contains(ShardLocationKey(targetShard)))
                    {
                        stagedNewShards.Add(targetShard);
                    }

                    updatedShards.Add(targetShard);
                }

                if (updatedShards.Count < manifest.Erasure.WriteQuorum
                    || !CanRecoverErasureData(manifest.Erasure.DataShards, updatedShards.Select(shard => shard.SetIndex)))
                {
                    await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                    return false;
                }

                updatedParts.Add(part with { Shards = updatedShards.OrderBy(shard => shard.SetIndex).ToArray() });
            }

            var updatedManifest = manifest with { Parts = updatedParts };
            var changed = wroteShard || !ManifestShardLocationsEqual(manifest, updatedManifest);
            if (!changed)
            {
                return false;
            }

            await WriteManifestCopiesAsync(
                record.BucketName,
                record.ObjectId,
                updatedManifest,
                updatedParts.SelectMany(part => part.Shards).ToArray(),
                cancellationToken);
            await DeleteSupersededObjectFilesAsync(manifest, updatedManifest, cancellationToken);
            return true;
        }
        catch
        {
            await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> TryRebalanceFullCopyObjectAsync(
        XlObjectRecord record,
        XlObjectManifest manifest,
        ClusterTopology topology,
        CancellationToken cancellationToken)
    {
        var onlineDiskCount = topology.Nodes
            .Where(node => string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
            .SelectMany(node => node.Disks)
            .Count(disk => string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
        if (onlineDiskCount <= 0)
        {
            return false;
        }

        var oldLocations = manifest.Parts
            .SelectMany(part => part.Shards)
            .Select(ShardLocationKey)
            .ToHashSet(StringComparer.Ordinal);
        var stagedNewShards = new List<XlShardManifest>();
        var wroteShard = false;
        var updatedParts = new List<XlPartManifest>(manifest.Parts.Count);
        try
        {
            foreach (var part in manifest.Parts)
            {
                var desiredReplicas = Math.Min(
                    Math.Min(onlineDiskCount, 16),
                    Math.Max(Math.Max(1, manifest.Erasure.WriteQuorum), part.Shards.Count));
                if (desiredReplicas < Math.Max(1, manifest.Erasure.WriteQuorum))
                {
                    await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                    return false;
                }

                var plannedTargets = TryPlanRebalanceTargets(
                    record.BucketName,
                    record.Key,
                    record.VersionId,
                    desiredReplicas,
                    part.Size,
                    topology);
                if (plannedTargets is null || plannedTargets.Count != desiredReplicas)
                {
                    await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                    return false;
                }

                var existingByDisk = part.Shards
                    .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var source = await TryResolveFullCopyRebalanceSourceAsync(part, cancellationToken);
                if (source is null)
                {
                    await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                    return false;
                }

                try
                {
                    var updatedShards = new List<XlShardManifest>(desiredReplicas);
                    for (var replicaIndex = 0; replicaIndex < plannedTargets.Count; replicaIndex++)
                    {
                        var target = plannedTargets[replicaIndex];
                        if (existingByDisk.TryGetValue(target.DiskId, out var existing))
                        {
                            var probe = await ProbeShardAsync(existing, verifyChecksum: true, cancellationToken);
                            if (probe.Status == ShardProbeStatus.Available)
                            {
                                updatedShards.Add(existing with { SetIndex = replicaIndex });
                                continue;
                            }
                        }

                        var relativePath = existingByDisk.TryGetValue(target.DiskId, out var reusable)
                            ? reusable.RelativePath
                            : source.Shard.RelativePath;
                        var targetShard = new XlShardManifest(
                            target.DiskId,
                            replicaIndex,
                            relativePath,
                            part.Size,
                            part.ChecksumSha256);
                        var targetProbe = await ProbeShardAsync(targetShard, verifyChecksum: true, cancellationToken);
                        if (targetProbe.Status != ShardProbeStatus.Available)
                        {
                            await CommitCopiedShardToTargetAsync(source.Path, targetShard, cancellationToken);
                            wroteShard = true;
                            if (!oldLocations.Contains(ShardLocationKey(targetShard)))
                            {
                                stagedNewShards.Add(targetShard);
                            }
                        }

                        updatedShards.Add(targetShard);
                    }

                    if (updatedShards.Count < Math.Max(1, manifest.Erasure.WriteQuorum))
                    {
                        await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
                        return false;
                    }

                    updatedParts.Add(part with { Shards = updatedShards.OrderBy(shard => shard.SetIndex).ToArray() });
                }
                finally
                {
                    if (source.DeleteOnDispose)
                    {
                        DeleteFileQuietly(source.Path);
                    }
                }
            }

            var updatedManifest = manifest with { Parts = updatedParts };
            var changed = wroteShard || !ManifestShardLocationsEqual(manifest, updatedManifest);
            if (!changed)
            {
                return false;
            }

            await WriteManifestCopiesAsync(
                record.BucketName,
                record.ObjectId,
                updatedManifest,
                updatedParts.SelectMany(part => part.Shards).ToArray(),
                cancellationToken);
            await DeleteSupersededObjectFilesAsync(manifest, updatedManifest, cancellationToken);
            return true;
        }
        catch
        {
            await RollbackRebalanceAsync(stagedNewShards, manifest, CancellationToken.None);
            throw;
        }
    }

    private IReadOnlyList<ShardWriteTarget>? TryPlanRebalanceTargets(
        string bucketName,
        string objectKey,
        string? versionId,
        int replicaCount,
        long contentLength,
        ClusterTopology topology)
    {
        try
        {
            var plan = _placementPlanner.PlanPlacement(
                CreatePlacementRequest(
                    bucketName,
                    objectKey,
                    versionId,
                    replicaCount,
                    contentLength),
                topology);
            var nodesById = topology.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            var selectedDisks = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<ShardWriteTarget>(replicaCount);
            foreach (var replica in plan.Replicas.OrderBy(replica => replica.ReplicaIndex))
            {
                if (!selectedDisks.Add(replica.DiskId))
                {
                    return null;
                }

                var localDisk = _disks.FirstOrDefault(disk => string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal));
                if (localDisk is not null)
                {
                    if (!localDisk.Online)
                    {
                        return null;
                    }

                    targets.Add(new ShardWriteTarget(replica.DiskId, localDisk, null));
                    continue;
                }

                if (!_shardTransport.Enabled
                    || !nodesById.TryGetValue(replica.NodeId, out var node)
                    || !string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                {
                    return null;
                }

                var remoteDisk = node.Disks.FirstOrDefault(disk =>
                    string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal)
                    && string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
                if (remoteDisk is null)
                {
                    return null;
                }

                targets.Add(new ShardWriteTarget(replica.DiskId, null, node));
            }

            return targets.Count == replicaCount ? targets : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ResolvedRebalanceShard?> TryResolveFullCopyRebalanceSourceAsync(
        XlPartManifest part,
        CancellationToken cancellationToken)
    {
        foreach (var shard in part.Shards.OrderBy(shard => shard.SetIndex))
        {
            var source = await TryResolveVerifiedRebalanceSourceAsync(shard, cancellationToken);
            if (source is not null)
            {
                return source;
            }
        }

        return null;
    }

    private async Task<bool> TryCopyShardToTargetAsync(
        XlShardManifest sourceShard,
        XlShardManifest targetShard,
        CancellationToken cancellationToken)
    {
        var source = await TryResolveVerifiedRebalanceSourceAsync(sourceShard, cancellationToken);
        if (source is null)
        {
            return false;
        }

        try
        {
            await CommitCopiedShardToTargetAsync(source.Path, targetShard, cancellationToken);
            return true;
        }
        finally
        {
            if (source.DeleteOnDispose)
            {
                DeleteFileQuietly(source.Path);
            }
        }
    }

    private async Task<ResolvedRebalanceShard?> TryResolveVerifiedRebalanceSourceAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        var resolved = await TryResolveReadableShardPathAsync(shard, cancellationToken);
        if (resolved.Path is null || resolved.ChecksumMismatch)
        {
            if (resolved.DeleteOnDispose && resolved.Path is not null)
            {
                DeleteFileQuietly(resolved.Path);
            }

            return null;
        }

        var file = new FileInfo(resolved.Path);
        if (file.Length != shard.Size
            || !string.Equals(await ComputeFileSha256Async(resolved.Path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (resolved.DeleteOnDispose)
            {
                DeleteFileQuietly(resolved.Path);
            }

            return null;
        }

        return new ResolvedRebalanceShard(shard, resolved.Path, resolved.DeleteOnDispose);
    }

    private async Task CommitCopiedShardToTargetAsync(
        string sourcePath,
        XlShardManifest targetShard,
        CancellationToken cancellationToken)
    {
        var sourceFile = new FileInfo(sourcePath);
        if (sourceFile.Length != targetShard.Size
            || !string.Equals(await ComputeFileSha256Async(sourcePath, cancellationToken), targetShard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Rebalance source shard failed verification.", 503);
        }

        var disk = _disks.FirstOrDefault(item => item.DiskId == targetShard.DiskId);
        if (disk is not null)
        {
            var targetPath = Path.Combine(disk.RootPath, targetShard.RelativePath);
            if (string.Equals(NormalizeStoragePath(sourcePath), NormalizeStoragePath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var tempPath = Path.Combine(
                Path.GetDirectoryName(targetPath)!,
                "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".rebalance.tmp");
            try
            {
                await using (var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    XlStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var temp = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    XlStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(temp, XlStreamBufferSize, cancellationToken);
                }

                var tempFile = new FileInfo(tempPath);
                if (tempFile.Length != targetShard.Size
                    || !string.Equals(await ComputeFileSha256Async(tempPath, cancellationToken), targetShard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MeansException(MeansErrorCodes.InvalidRequest, "Rebalanced shard failed verification.", 503);
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                DeleteFileQuietly(tempPath);
            }

            return;
        }

        if (!_shardTransport.Enabled)
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Cluster shard transport is not configured for rebalance.", 503);
        }

        var node = await TryFindRemoteNodeForDiskAsync(targetShard.DiskId, cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.SlowDown, "Remote rebalance target is not online.", 503);
        await using var content = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            XlStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await _shardTransport.WriteShardAsync(
            node,
            targetShard.DiskId,
            targetShard.RelativePath,
            content,
            targetShard.Size,
            targetShard.ChecksumSha256,
            cancellationToken);
    }

    private async Task DeleteSupersededObjectFilesAsync(
        XlObjectManifest oldManifest,
        XlObjectManifest updatedManifest,
        CancellationToken cancellationToken)
    {
        await DeleteSupersededManifestReplicasAsync(oldManifest, updatedManifest, cancellationToken);
        var retainedShardLocations = updatedManifest.Parts
            .SelectMany(part => part.Shards)
            .Select(ShardLocationKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var shard in oldManifest.Parts.SelectMany(part => part.Shards))
        {
            if (retainedShardLocations.Contains(ShardLocationKey(shard)))
            {
                continue;
            }

            await DeleteShardLocationQuietlyAsync(shard, cancellationToken);
        }
    }

    private async Task DeleteSupersededManifestReplicasAsync(
        XlObjectManifest oldManifest,
        XlObjectManifest updatedManifest,
        CancellationToken cancellationToken)
    {
        var (bytes, checksum) = SerializeManifestCopy(updatedManifest);
        var retainedDiskIds = SelectManifestReplicaTargets(updatedManifest, bytes.Length, checksum)
            .Select(shard => shard.DiskId)
            .ToHashSet(StringComparer.Ordinal);
        var relativePath = ObjectManifestRelativePath(oldManifest.BucketName, oldManifest.ObjectId);
        foreach (var disk in _disks)
        {
            if (!retainedDiskIds.Contains(disk.DiskId))
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, relativePath));
            }
        }

        var oldRemoteDiskIds = oldManifest.Parts
            .SelectMany(part => part.Shards)
            .Select(shard => shard.DiskId)
            .Where(diskId => _disks.All(disk => !string.Equals(disk.DiskId, diskId, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var diskId in oldRemoteDiskIds)
        {
            if (retainedDiskIds.Contains(diskId) || !_shardTransport.Enabled)
            {
                continue;
            }

            try
            {
                var node = await TryFindRemoteNodeForDiskAsync(diskId, cancellationToken);
                if (node is not null)
                {
                    await _shardTransport.DeleteManifestAsync(node, diskId, relativePath, cancellationToken);
                }
            }
            catch
            {
            }
        }
    }

    private async Task RollbackRebalanceAsync(
        IReadOnlyList<XlShardManifest> stagedNewShards,
        XlObjectManifest oldManifest,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteManifestCopiesAsync(
                oldManifest.BucketName,
                oldManifest.ObjectId,
                oldManifest,
                oldManifest.Parts.SelectMany(part => part.Shards).ToArray(),
                cancellationToken);
        }
        catch
        {
        }

        foreach (var shard in stagedNewShards)
        {
            await DeleteShardLocationQuietlyAsync(shard, cancellationToken);
        }
    }

    private async Task DeleteShardLocationQuietlyAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        try
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is not null)
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
                return;
            }

            if (!_shardTransport.Enabled)
            {
                return;
            }

            var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
            if (node is not null)
            {
                await _shardTransport.DeleteShardAsync(node, shard.DiskId, shard.RelativePath, cancellationToken);
            }
        }
        catch
        {
        }
    }

    private static bool ManifestShardLocationsEqual(
        XlObjectManifest left,
        XlObjectManifest right)
    {
        var leftLocations = left.Parts
            .SelectMany(part => part.Shards)
            .Select(ShardLocationKey)
            .ToHashSet(StringComparer.Ordinal);
        var rightLocations = right.Parts
            .SelectMany(part => part.Shards)
            .Select(ShardLocationKey)
            .ToHashSet(StringComparer.Ordinal);
        return leftLocations.SetEquals(rightLocations);
    }

    private static string ShardLocationKey(XlShardManifest shard)
    {
        return shard.DiskId
            + "\0"
            + shard.RelativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public async Task<ObjectScrubResult> ScrubObjectReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rows = await Db.ScanPrefixAsync(Keys.CurrentObjectGlobalPrefix, Math.Clamp(maxItems, 1, 10_000), null, cancellationToken);
        var checkedReplicas = 0;
        var missing = 0;
        var corrupt = 0;
        var queued = 0;
        foreach (var row in rows)
        {
            var record = Deserialize<XlObjectRecord>(row.Value);
            if (record.IsDeleteMarker)
            {
                continue;
            }

            var manifest = await TryReadManifestAsync(record, cancellationToken);
            if (manifest is null)
            {
                missing++;
                await QueueHealAsync(ToObjectInfo(record), "ManifestMissing", cancellationToken);
                queued++;
                continue;
            }

            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                var probe = await ProbeShardAsync(shard, verifyChecksum: true, cancellationToken);
                if (probe.Status == ShardProbeStatus.Missing)
                {
                    missing++;
                    await QueueHealAsync(ToObjectInfo(record), "ShardMissing", cancellationToken);
                    queued++;
                    continue;
                }

                checkedReplicas++;
                if (probe.Status == ShardProbeStatus.Corrupt)
                {
                    corrupt++;
                    await QueueHealAsync(ToObjectInfo(record), "ShardChecksumMismatch", cancellationToken);
                    queued++;
                }
            }
        }

        return new ObjectScrubResult(checkedReplicas, missing, corrupt, queued);
    }

    private async Task<ShardProbeResult> ProbeShardAsync(
        XlShardManifest shard,
        bool verifyChecksum,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        if (disk is not null)
        {
            var path = Path.Combine(disk.RootPath, shard.RelativePath);
            if (!File.Exists(path))
            {
                return new ShardProbeResult(ShardProbeStatus.Missing);
            }

            var file = new FileInfo(path);
            if (file.Length != shard.Size)
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            if (verifyChecksum
                && !string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            return new ShardProbeResult(ShardProbeStatus.Available);
        }

        if (!_shardTransport.Enabled)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }

        var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
        if (node is null)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }

        try
        {
            var stat = await _shardTransport.StatShardAsync(node, shard.DiskId, shard.RelativePath, cancellationToken);
            if (stat.Length != shard.Size)
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            if (verifyChecksum
                && !string.Equals(stat.ChecksumSha256, shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            return new ShardProbeResult(ShardProbeStatus.Available);
        }
        catch (MeansException ex) when (ex.StatusCode == 404)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }
    }

    private ClusterTopology BuildLocalTopology(DateTimeOffset offlineBeforeUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var nodeStatus = now < offlineBeforeUtc.ToUniversalTime() ? ClusterNodeStatuses.Offline : ClusterNodeStatuses.Online;
        var cluster = new StorageClusterInfo(
            string.IsNullOrWhiteSpace(_options.DeploymentId) ? "local-xlfs" : _options.DeploymentId,
            "Local XlFs Cluster",
            now);
        var disks = _disks.Select(disk => new StorageDiskInfo(
            disk.DiskId,
            "local",
            _options.SetId,
            disk.RootPath,
            disk.TotalBytes,
            disk.AvailableBytes,
            nodeStatus == ClusterNodeStatuses.Online && disk.Online ? StorageDiskStatuses.Online : StorageDiskStatuses.Offline,
            now)).ToArray();
        var node = new ClusterNodeInfo(
            "local",
            cluster.ClusterId,
            Environment.MachineName,
            "local",
            nodeStatus,
            now,
            now,
            disks,
            NormalizeFaultDomain("", "local"));
        var online = disks.Where(disk => disk.Status == StorageDiskStatuses.Online).ToArray();
        var pool = new StoragePoolInfo(
            _options.SetId,
            cluster.ClusterId,
            _options.SetId,
            now,
            1,
            disks.Length,
            online.Sum(disk => disk.TotalBytes),
            online.Sum(disk => disk.AvailableBytes));
        return new ClusterTopology(cluster, [node], [pool]);
    }

    private static string NormalizeFaultDomain(string? faultDomain, string nodeId)
    {
        return string.IsNullOrWhiteSpace(faultDomain)
            ? nodeId.Trim()
            : faultDomain.Trim();
    }

    private async Task<XlObjectManifest?> TryReadManifestAsync(
        XlObjectRecord record,
        CancellationToken cancellationToken)
    {
        foreach (var disk in _disks)
        {
            var path = Path.Combine(disk.RootPath, ObjectManifestRelativePath(record.BucketName, record.ObjectId));
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    XlJsonContext.Default.XlObjectManifest);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<bool> RepairObjectShardsAsync(XlObjectRecord record, CancellationToken cancellationToken)
    {
        var manifest = await TryReadManifestAsync(record, cancellationToken);
        if (manifest is null || manifest.Parts.Count == 0)
        {
            return false;
        }

        if (IsReedSolomonErasure(manifest))
        {
            return await RepairReedSolomonObjectShardsAsync(record, manifest, cancellationToken);
        }

        return await RepairFullCopyObjectShardsAsync(record, manifest, cancellationToken);
    }

    private async Task<bool> RepairFullCopyObjectShardsAsync(
        XlObjectRecord record,
        XlObjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var updatedParts = new List<XlPartManifest>(manifest.Parts.Count);
        foreach (var part in manifest.Parts)
        {
            var source = await TryResolveFullCopyRebalanceSourceAsync(part, cancellationToken);
            if (source is null)
            {
                return false;
            }

            try
            {
                var shards = part.Shards
                    .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var shard in part.Shards)
                {
                    var probe = await ProbeShardAsync(shard, verifyChecksum: true, cancellationToken);
                    if (probe.Status == ShardProbeStatus.Available)
                    {
                        continue;
                    }

                    await CommitCopiedShardToTargetAsync(source.Path, shard, cancellationToken);
                    changed = true;
                }

                var targets = await TryPlanFullCopyTargetsAsync(
                        record.BucketName,
                        record.Key,
                        record.VersionId,
                        part.Size,
                        cancellationToken)
                    ?? PlanLocalFullCopyTargets();
                foreach (var target in targets)
                {
                    if (shards.ContainsKey(target.DiskId))
                    {
                        continue;
                    }

                    var shard = new XlShardManifest(
                        target.DiskId,
                        target.SetIndex,
                        RepairedPartRelativePath(record.BucketName, record.ObjectId, part.Name, target.SetIndex),
                        part.Size,
                        part.ChecksumSha256);
                    await CommitCopiedShardToTargetAsync(source.Path, shard, cancellationToken);
                    shards[target.DiskId] = shard;
                    changed = true;
                }

                var available = 0;
                foreach (var shard in shards.Values)
                {
                    var probe = await ProbeShardAsync(shard, verifyChecksum: true, cancellationToken);
                    if (probe.Status == ShardProbeStatus.Available)
                    {
                        available++;
                    }
                }

                if (available < WriteQuorum)
                {
                    return false;
                }

                updatedParts.Add(part with { Shards = shards.Values.OrderBy(shard => shard.SetIndex).ToArray() });
            }
            finally
            {
                if (source.DeleteOnDispose)
                {
                    DeleteFileQuietly(source.Path);
                }
            }
        }

        if (!changed)
        {
            await WriteManifestCopiesAsync(
                record.BucketName,
                record.ObjectId,
                manifest,
                manifest.Parts.SelectMany(part => part.Shards).ToArray(),
                cancellationToken);
            return true;
        }

        var updatedManifest = manifest with { Parts = updatedParts };
        await WriteManifestCopiesAsync(
            record.BucketName,
            record.ObjectId,
            updatedManifest,
            updatedParts.SelectMany(part => part.Shards).ToArray(),
            cancellationToken);
        return true;
    }

    private async Task<bool> RepairReedSolomonObjectShardsAsync(
        XlObjectRecord record,
        XlObjectManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var part in manifest.Parts)
        {
            if (part.Shards.Count < manifest.Erasure.DataShards)
            {
                return false;
            }

            foreach (var shard in part.Shards.OrderBy(shard => shard.SetIndex))
            {
                var probe = await ProbeShardAsync(shard, verifyChecksum: true, cancellationToken);
                if (probe.Status == ShardProbeStatus.Available)
                {
                    continue;
                }

                if (!await TryRepairReedSolomonShardAsync(manifest.Erasure, part, shard, cancellationToken))
                {
                    return false;
                }
            }
        }

        await WriteManifestCopiesAsync(
            record.BucketName,
            record.ObjectId,
            manifest,
            manifest.Parts.SelectMany(part => part.Shards).ToArray(),
            cancellationToken);

        return true;
    }

    private async Task<int> CountUnavailableManifestReplicasAsync(
        XlObjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var (jsonBytes, checksum) = SerializeManifestCopy(manifest);
        var unavailable = 0;
        foreach (var replica in SelectManifestReplicaTargets(manifest, jsonBytes.Length, checksum))
        {
            var probe = await ProbeManifestReplicaAsync(replica, cancellationToken);
            if (probe.Status != ShardProbeStatus.Available)
            {
                unavailable++;
            }
        }

        return unavailable;
    }

    private async Task<ShardProbeResult> ProbeManifestReplicaAsync(
        XlShardManifest replica,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == replica.DiskId);
        if (disk is not null)
        {
            var path = Path.Combine(disk.RootPath, replica.RelativePath);
            if (!File.Exists(path))
            {
                return new ShardProbeResult(ShardProbeStatus.Missing);
            }

            var file = new FileInfo(path);
            if (file.Length != replica.Size
                || !string.Equals(await ComputeFileSha256Async(path, cancellationToken), replica.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            return new ShardProbeResult(ShardProbeStatus.Available);
        }

        if (!_shardTransport.Enabled)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }

        var node = await TryFindRemoteNodeForDiskAsync(replica.DiskId, cancellationToken);
        if (node is null)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }

        try
        {
            var stat = await _shardTransport.StatManifestAsync(
                node,
                replica.DiskId,
                replica.RelativePath,
                cancellationToken);
            if (stat.Length != replica.Size
                || !string.Equals(stat.ChecksumSha256, replica.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ShardProbeResult(ShardProbeStatus.Corrupt);
            }

            return new ShardProbeResult(ShardProbeStatus.Available);
        }
        catch (MeansException ex) when (ex.StatusCode == 404)
        {
            return new ShardProbeResult(ShardProbeStatus.Missing);
        }
    }

    private IReadOnlyList<XlShardManifest> SelectManifestReplicaTargets(
        XlObjectManifest manifest,
        long manifestLength,
        string checksumSha256)
    {
        var relativePath = ObjectManifestRelativePath(manifest.BucketName, manifest.ObjectId);
        var targets = manifest.Parts
            .SelectMany(part => part.Shards)
            .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
            .Select(group =>
            {
                var shard = group.OrderBy(item => item.SetIndex).First();
                return new XlShardManifest(
                    shard.DiskId,
                    shard.SetIndex,
                    relativePath,
                    manifestLength,
                    checksumSha256);
            })
            .ToList();
        if (targets.All(shard => _disks.All(disk => !string.Equals(disk.DiskId, shard.DiskId, StringComparison.Ordinal)))
            && _disks.FirstOrDefault(disk => disk.Online) is { } localDisk)
        {
            targets.Add(new XlShardManifest(
                localDisk.DiskId,
                localDisk.SetIndex,
                relativePath,
                manifestLength,
                checksumSha256));
        }

        return targets;
    }

    private static (byte[] Bytes, string ChecksumSha256) SerializeManifestCopy(XlObjectManifest manifest)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(manifest, XlJsonContext.Default.XlObjectManifest);
        var bytes = Encoding.UTF8.GetBytes(json);
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private async Task<bool> TryRepairReedSolomonShardAsync(
        XlErasureInfo erasure,
        XlPartManifest part,
        XlShardManifest targetShard,
        CancellationToken cancellationToken)
    {
        var allShards = part.Shards.ToDictionary(shard => shard.SetIndex);
        var tempPaths = new List<string>();
        string? repairedPath = null;
        try
        {
            repairedPath = RepairedShardTempPath(targetShard);
            Directory.CreateDirectory(Path.GetDirectoryName(repairedPath)!);

            if (targetShard.SetIndex < erasure.DataShards)
            {
                var available = await SelectHealthyErasureShardsForRepairAsync(
                    erasure,
                    allShards,
                    targetShard.SetIndex,
                    tempPaths,
                    cancellationToken);
                if (available.Count < erasure.DataShards)
                {
                    return false;
                }

                var decodeMatrix = BuildDecodeMatrix(erasure.DataShards, available);
                await using (var output = new FileStream(
                    repairedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    ErasureBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await WriteDecodedDataShardAsync(
                        output,
                        targetShard.SetIndex,
                        available,
                        decodeMatrix,
                        targetShard.Size,
                        targetShard.Size,
                        cancellationToken);
                }
            }
            else
            {
                var dataShards = await ResolveHealthyDataShardsForParityRepairAsync(
                    erasure,
                    allShards,
                    tempPaths,
                    cancellationToken);
                if (dataShards is null)
                {
                    return false;
                }

                await WriteParityShardAsync(
                    repairedPath,
                    targetShard.SetIndex - erasure.DataShards,
                    dataShards,
                    targetShard.Size,
                    cancellationToken);
            }

            var file = new FileInfo(repairedPath);
            if (file.Length != targetShard.Size
                || !string.Equals(await ComputeFileSha256Async(repairedPath, cancellationToken), targetShard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await CommitRepairedShardAsync(targetShard, repairedPath, cancellationToken);
            repairedPath = null;
            return true;
        }
        finally
        {
            if (repairedPath is not null)
            {
                DeleteFileQuietly(repairedPath);
            }

            foreach (var path in tempPaths.Distinct(StoragePathComparer))
            {
                DeleteFileQuietly(path);
            }
        }
    }

    private async Task<IReadOnlyList<ResolvedErasureShard>> SelectHealthyErasureShardsForRepairAsync(
        XlErasureInfo erasure,
        IReadOnlyDictionary<int, XlShardManifest> allShards,
        int excludedShardIndex,
        List<string> tempPaths,
        CancellationToken cancellationToken)
    {
        var available = new List<ResolvedErasureShard>(erasure.DataShards);
        for (var shardIndex = 0; shardIndex < erasure.DataShards + erasure.ParityShards; shardIndex++)
        {
            if (available.Count == erasure.DataShards)
            {
                break;
            }

            if (shardIndex == excludedShardIndex
                || !allShards.TryGetValue(shardIndex, out var shard))
            {
                continue;
            }

            var resolved = await TryResolveVerifiedRepairShardAsync(shardIndex, shard, cancellationToken);
            if (resolved is null)
            {
                continue;
            }

            if (resolved.DeleteOnDispose)
            {
                tempPaths.Add(resolved.Path);
            }

            available.Add(resolved);
        }

        return available;
    }

    private async Task<IReadOnlyList<ResolvedErasureShard>?> ResolveHealthyDataShardsForParityRepairAsync(
        XlErasureInfo erasure,
        IReadOnlyDictionary<int, XlShardManifest> allShards,
        List<string> tempPaths,
        CancellationToken cancellationToken)
    {
        var dataShards = new List<ResolvedErasureShard>(erasure.DataShards);
        for (var dataIndex = 0; dataIndex < erasure.DataShards; dataIndex++)
        {
            if (!allShards.TryGetValue(dataIndex, out var shard))
            {
                return null;
            }

            var resolved = await TryResolveVerifiedRepairShardAsync(dataIndex, shard, cancellationToken);
            if (resolved is null)
            {
                return null;
            }

            if (resolved.DeleteOnDispose)
            {
                tempPaths.Add(resolved.Path);
            }

            dataShards.Add(resolved);
        }

        return dataShards;
    }

    private async Task<ResolvedErasureShard?> TryResolveVerifiedRepairShardAsync(
        int shardIndex,
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeShardAsync(shard, verifyChecksum: true, cancellationToken);
        if (probe.Status != ShardProbeStatus.Available)
        {
            return null;
        }

        var resolved = await TryResolveReadableShardPathAsync(shard, cancellationToken);
        if (resolved.Path is null || resolved.ChecksumMismatch)
        {
            return null;
        }

        if (!string.Equals(await ComputeFileSha256Async(resolved.Path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (resolved.DeleteOnDispose)
            {
                DeleteFileQuietly(resolved.Path);
            }

            return null;
        }

        return new ResolvedErasureShard(shardIndex, resolved.Path, resolved.DeleteOnDispose);
    }

    private async Task WriteParityShardAsync(
        string destinationPath,
        int parityIndex,
        IReadOnlyList<ResolvedErasureShard> dataShards,
        long shardLength,
        CancellationToken cancellationToken)
    {
        var outputBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ErasureBufferSize);
        var readBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ErasureBufferSize);
        var streams = new List<FileStream>(dataShards.Count);
        try
        {
            foreach (var shard in dataShards.OrderBy(shard => shard.ShardIndex))
            {
                streams.Add(new FileStream(
                    shard.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    ErasureBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ErasureBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var remaining = shardLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toProcess = (int)Math.Min(outputBuffer.Length, remaining);
                Array.Clear(outputBuffer, 0, toProcess);
                for (var dataIndex = 0; dataIndex < streams.Count; dataIndex++)
                {
                    await streams[dataIndex].ReadExactlyAsync(readBuffer.AsMemory(0, toProcess), cancellationToken);
                    XorMultiply(outputBuffer, readBuffer, ParityCoefficient(parityIndex, dataIndex), toProcess);
                }

                await output.WriteAsync(outputBuffer.AsMemory(0, toProcess), cancellationToken);
                remaining -= toProcess;
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }

            System.Buffers.ArrayPool<byte>.Shared.Return(outputBuffer);
            System.Buffers.ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private string RepairedShardTempPath(XlShardManifest shard)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        var tempRoot = disk is not null
            ? Path.Combine(disk.RootPath, ".means.sys", "tmp", "repair")
            : Path.GetDirectoryName(TempPath("repair.tmp"))!;
        return Path.Combine(tempRoot, "repair-" + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private async Task CommitRepairedShardAsync(
        XlShardManifest shard,
        string repairedPath,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        if (disk is not null)
        {
            var targetPath = Path.Combine(disk.RootPath, shard.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(repairedPath, targetPath, overwrite: true);
            return;
        }

        if (!_shardTransport.Enabled)
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Cluster shard transport is not configured for remote shard repair.", 503);
        }

        var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.SlowDown, "Remote shard repair target is not online.", 503);
        await using var content = new FileStream(
            repairedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            XlStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await _shardTransport.WriteShardAsync(
            node,
            shard.DiskId,
            shard.RelativePath,
            content,
            shard.Size,
            shard.ChecksumSha256,
            cancellationToken);
        DeleteFileQuietly(repairedPath);
    }

    private static string RepairedPartRelativePath(string bucketName, string objectId, string partName, int setIndex)
    {
        return Path.Combine("objects", BucketHash(bucketName), objectId, partName + ".part." + setIndex.ToString("D2"));
    }

    private async Task<HashSet<string>> ReadReferencedStoragePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StoragePathComparer);
        foreach (var row in await Db.ScanPrefixAsync("v:", 100_000, null, cancellationToken))
        {
            var record = Deserialize<XlObjectRecord>(row.Value);
            await AddObjectPathsAsync(record);
        }

        foreach (var row in await Db.ScanPrefixAsync(Keys.CurrentObjectGlobalPrefix, 100_000, null, cancellationToken))
        {
            var record = Deserialize<XlObjectRecord>(row.Value);
            await AddObjectPathsAsync(record);
        }

        foreach (var row in await Db.ScanPrefixAsync("mpp:", 100_000, null, cancellationToken))
        {
            var part = Deserialize<XlMultipartPartRecord>(row.Value);
            foreach (var shard in part.Shards)
            {
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (disk is not null)
                {
                    paths.Add(NormalizeStoragePath(Path.Combine(disk.RootPath, shard.RelativePath)));
                }
            }
        }

        return paths;

        async Task AddObjectPathsAsync(XlObjectRecord record)
        {
            foreach (var disk in _disks)
            {
                var manifestPath = Path.Combine(disk.RootPath, ObjectManifestRelativePath(record.BucketName, record.ObjectId));
                if (File.Exists(manifestPath))
                {
                    paths.Add(NormalizeStoragePath(manifestPath));
                }
            }

            var manifest = await TryReadManifestAsync(record, cancellationToken);
            if (manifest is null)
            {
                return;
            }

            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (disk is not null)
                {
                    paths.Add(NormalizeStoragePath(Path.Combine(disk.RootPath, shard.RelativePath)));
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string path, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(path, searchPattern, searchOption).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch
                {
                    break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static bool IsOlderThan(string path, DateTimeOffset cutoffUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < cutoffUtc.UtcDateTime;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeStoragePath(string path) => Path.GetFullPath(path);

    private static StringComparer StoragePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeNodeStatus(string status)
    {
        return string.Equals(status, ClusterNodeStatuses.Online, StringComparison.OrdinalIgnoreCase)
            ? ClusterNodeStatuses.Online
            : ClusterNodeStatuses.Offline;
    }

    private static string NormalizeDiskStatus(string status)
    {
        return string.Equals(status, StorageDiskStatuses.Online, StringComparison.OrdinalIgnoreCase)
            ? StorageDiskStatuses.Online
            : StorageDiskStatuses.Offline;
    }

    private static void ValidateRegistration(ClusterNodeRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.ClusterId)
            || string.IsNullOrWhiteSpace(registration.ClusterName)
            || string.IsNullOrWhiteSpace(registration.NodeId)
            || string.IsNullOrWhiteSpace(registration.HostName)
            || string.IsNullOrWhiteSpace(registration.Endpoint)
            || string.IsNullOrWhiteSpace(registration.PoolId)
            || string.IsNullOrWhiteSpace(registration.PoolName)
            || registration.Disks.Count == 0
            || registration.Disks.Any(disk =>
                string.IsNullOrWhiteSpace(disk.DiskId)
                || string.IsNullOrWhiteSpace(disk.PoolId)
                || string.IsNullOrWhiteSpace(disk.MountPath)))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Cluster registration fields are required.", 400);
        }
    }

    private static ErasureCodingProfile ValidateErasureCodingProfile(ErasureCodingProfile profile)
    {
        var profileId = NormalizeErasureCodingProfileId(profile.ProfileId);
        if (profile.DataShards is < 1 or > 32)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC data shards must be between 1 and 32.", 400);
        }

        if (profile.ParityShards is < 0 or > 16)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC parity shards must be between 0 and 16.", 400);
        }

        if (profile.DataShards + profile.ParityShards > 48)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC total shards must not exceed 48.", 400);
        }

        if (profile.CellSizeBytes is < 65536 or > 67108864 || !IsPowerOfTwo(profile.CellSizeBytes))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC cell size must be a power of two between 64 KiB and 64 MiB.", 400);
        }

        return profile with { ProfileId = profileId };
    }

    private static string NormalizeErasureCodingProfileId(string profileId)
    {
        var normalized = profileId.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 64
            || normalized[0] is '-' or '.'
            || normalized[^1] is '-' or '.'
            || normalized.Any(character =>
                character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid EC profile id.", 400);
        }

        return normalized;
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private enum ShardProbeStatus
    {
        Available,
        Missing,
        Corrupt
    }

    private sealed record ResolvedRebalanceShard(XlShardManifest Shard, string Path, bool DeleteOnDispose);

    private sealed record ShardProbeResult(ShardProbeStatus Status);
}
