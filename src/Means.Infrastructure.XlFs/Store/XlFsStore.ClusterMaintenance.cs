using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task RegisterNodeAsync(ClusterNodeRegistration registration, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await EnsureInitializedAsync(cancellationToken);
        var registeredAt = registration.RegisteredAt.ToUniversalTime();
        var cluster = new StorageClusterInfo(registration.ClusterId, registration.ClusterName, registeredAt);
        var node = new ClusterNodeInfo(
            registration.NodeId,
            registration.ClusterId,
            registration.HostName,
            registration.Endpoint,
            ClusterNodeStatuses.Online,
            registeredAt,
            registeredAt,
            registration.Disks.Select(disk => new StorageDiskInfo(
                disk.DiskId,
                registration.NodeId,
                disk.PoolId,
                disk.MountPath,
                Math.Max(0, disk.TotalBytes),
                Math.Max(0, disk.AvailableBytes),
                NormalizeDiskStatus(disk.Status),
                registeredAt)).ToArray());

        await Db.PutBatchAsync([
            new LogDbMutation(Keys.ClusterInfo, Serialize(cluster), false),
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
        var cluster = await Db.GetJsonAsync<StorageClusterInfo>(Keys.ClusterInfo, cancellationToken);
        var nodeRows = await Db.ScanPrefixAsync(Keys.ClusterNodePrefix, 100_000, null, cancellationToken);
        var nodes = nodeRows.Select(row => Deserialize<ClusterNodeInfo>(row.Value))
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

        var pools = nodes.SelectMany(node => node.Disks)
            .GroupBy(disk => disk.PoolId, StringComparer.Ordinal)
            .Select(group =>
            {
                var online = group.Where(disk => string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal)).ToArray();
                return new StoragePoolInfo(
                    group.Key,
                    cluster.ClusterId,
                    group.Key,
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
        if (profiles.All(profile => profile.ProfileId != "xlfs-default"))
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

            var existingShards = 0;
            foreach (var shard in manifest.Parts.SelectMany(part => part.Shards))
            {
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (disk is null || !File.Exists(Path.Combine(disk.RootPath, shard.RelativePath)))
                {
                    missingFiles++;
                    continue;
                }

                existingShards++;
            }

            if (existingShards < WriteQuorum)
            {
                underReplicated++;
                if (repair)
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
        var rows = await Db.ScanPrefixAsync(Keys.HealPrefix, Math.Clamp(maxItems, 1, 10_000), null, cancellationToken);
        var repaired = 0;
        foreach (var row in rows)
        {
            var repair = Deserialize<Dictionary<string, string>>(row.Value);
            if (!repair.TryGetValue("bucket", out var bucket)
                || !repair.TryGetValue("key", out var key))
            {
                await Db.PutBatchAsync([new LogDbMutation(row.Key, null, true)], cancellationToken);
                continue;
            }

            try
            {
                var record = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucket, key), cancellationToken);
                if (record is not null && await RepairObjectShardsAsync(record, cancellationToken))
                {
                    repaired++;
                }

                await Db.PutBatchAsync([new LogDbMutation(row.Key, null, true)], cancellationToken);
            }
            catch
            {
                // Keep the heal item queued for a later pass.
            }
        }

        return repaired;
    }

    public Task<int> RebuildErasureCodedObjectsAsync(int maxItems, CancellationToken cancellationToken)
    {
        return RepairQueuedReplicasAsync(maxItems, cancellationToken);
    }

    public Task<int> RebalanceObjectReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
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
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                var path = disk is null ? null : Path.Combine(disk.RootPath, shard.RelativePath);
                if (path is null || !File.Exists(path))
                {
                    missing++;
                    await QueueHealAsync(ToObjectInfo(record), "ShardMissing", cancellationToken);
                    queued++;
                    continue;
                }

                checkedReplicas++;
                if (!string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    corrupt++;
                    await QueueHealAsync(ToObjectInfo(record), "ShardChecksumMismatch", cancellationToken);
                    queued++;
                }
            }
        }

        return new ObjectScrubResult(checkedReplicas, missing, corrupt, queued);
    }

    private ClusterTopology BuildLocalTopology(DateTimeOffset offlineBeforeUtc)
    {
        var now = DateTimeOffset.UtcNow;
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
            disk.Online ? StorageDiskStatuses.Online : StorageDiskStatuses.Offline,
            now)).ToArray();
        var node = new ClusterNodeInfo(
            "local",
            cluster.ClusterId,
            Environment.MachineName,
            "local",
            now < offlineBeforeUtc.ToUniversalTime() ? ClusterNodeStatuses.Offline : ClusterNodeStatuses.Online,
            now,
            now,
            disks);
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

        var changed = false;
        var updatedParts = new List<XlPartManifest>(manifest.Parts.Count);
        foreach (var part in manifest.Parts)
        {
            string? sourcePath = null;
            foreach (var shard in part.Shards)
            {
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                var path = disk is null ? null : Path.Combine(disk.RootPath, shard.RelativePath);
                if (path is not null
                    && File.Exists(path)
                    && string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    sourcePath = path;
                    break;
                }
            }

            if (sourcePath is null)
            {
                return false;
            }

            var shards = part.Shards.ToDictionary(shard => shard.DiskId, StringComparer.Ordinal);
            foreach (var disk in _disks.Where(disk => disk.Online))
            {
                if (shards.TryGetValue(disk.DiskId, out var existing)
                    && File.Exists(Path.Combine(disk.RootPath, existing.RelativePath)))
                {
                    continue;
                }

                var relative = RepairedPartRelativePath(record.BucketName, record.ObjectId, part.Name, disk.SetIndex);
                var target = Path.Combine(disk.RootPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourcePath, target, overwrite: true);
                shards[disk.DiskId] = new XlShardManifest(disk.DiskId, disk.SetIndex, relative, part.Size, part.ChecksumSha256);
                changed = true;
            }

            updatedParts.Add(part with { Shards = shards.Values.OrderBy(shard => shard.SetIndex).ToArray() });
        }

        if (!changed)
        {
            return true;
        }

        var updatedManifest = manifest with { Parts = updatedParts };
        var json = System.Text.Json.JsonSerializer.Serialize(updatedManifest, XlJsonContext.Default.XlObjectManifest);
        foreach (var shard in updatedParts.SelectMany(part => part.Shards).GroupBy(shard => shard.DiskId, StringComparer.Ordinal).Select(group => group.First()))
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is null)
            {
                continue;
            }

            var manifestPath = Path.Combine(disk.RootPath, ObjectManifestRelativePath(record.BucketName, record.ObjectId));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
        }

        return true;
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
}
