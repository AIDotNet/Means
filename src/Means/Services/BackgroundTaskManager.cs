using Means.Configuration;
using Means.Core;
using Means.Infrastructure.SqliteFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

public sealed class BackgroundTaskManager : IBackgroundTaskManager
{
    private readonly IStorageMaintenanceOperations _store;
    private readonly IClusterStore _clusterStore;
    private readonly IOptions<SqliteFsOptions> _storageOptions;
    private readonly IOptions<ClusterOptions> _clusterOptions;
    private readonly IBackgroundTaskRegistry _registry;
    private readonly ILogger<BackgroundTaskManager> _logger;
    private readonly IReadOnlyDictionary<string, ManagedBackgroundTask> _tasks;

    public BackgroundTaskManager(
        IStorageMaintenanceOperations store,
        IClusterStore clusterStore,
        IOptions<SqliteFsOptions> storageOptions,
        IOptions<ClusterOptions> clusterOptions,
        IBackgroundTaskRegistry registry,
        ILogger<BackgroundTaskManager> logger)
    {
        _store = store;
        _clusterStore = clusterStore;
        _storageOptions = storageOptions;
        _clusterOptions = clusterOptions;
        _registry = registry;
        _logger = logger;
        _tasks = BuildTasks().ToDictionary(task => task.Descriptor.TaskId, StringComparer.Ordinal);
        foreach (var task in _tasks.Values)
        {
            _registry.Register(task.Descriptor);
        }
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks()
    {
        return _registry.ListTasks();
    }

    public IReadOnlyList<BackgroundTaskGroup> ListGroups()
    {
        return ListTasks()
            .GroupBy(task => task.Category, StringComparer.Ordinal)
            .Select(group => new BackgroundTaskGroup(
                group.Key,
                CategoryName(group.Key),
                group.OrderBy(task => task.TaskId, StringComparer.Ordinal).ToArray()))
            .OrderBy(group => CategorySort(group.Category))
            .ThenBy(group => group.Category, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<BackgroundTaskRunRecord> ListHistory(string? taskId, int limit)
    {
        return _registry.ListHistory(taskId, limit);
    }

    public async Task<BackgroundTaskSnapshot> RunTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var normalizedTaskId = taskId.Trim();
        if (!_tasks.TryGetValue(normalizedTaskId, out var task))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Unknown background task.", 404);
        }

        if (!task.Descriptor.ManualRunSupported)
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "This background task cannot be run manually.", 409);
        }

        await _registry.RunAsync(task.Descriptor, task.Operation, cancellationToken);
        return _registry.GetTask(normalizedTaskId)
            ?? throw new MeansException(MeansErrorCodes.InvalidRequest, "Background task state was not recorded.", 500);
    }

    private IEnumerable<ManagedBackgroundTask> BuildTasks()
    {
        var storage = _storageOptions.Value;
        var cluster = _clusterOptions.Value;
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ClusterHeartbeat(cluster.HeartbeatIntervalSeconds),
            RefreshClusterNodeAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.DiskHealthIsolation(cluster.DiskHealthIntervalSeconds),
            RunDiskHealthIsolationAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ReplicaRepair(storage.ReplicaRepairIntervalSeconds),
            RunReplicaRepairAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ErasureCodingRepair(storage.ErasureCodingRepairIntervalSeconds),
            RunErasureCodingRepairAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ReplicaRebalance(storage.RebalanceIntervalSeconds),
            RunReplicaRebalanceAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.Lifecycle(storage.LifecycleIntervalSeconds),
            RunLifecycleAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ObjectScrub(storage.ScrubIntervalSeconds),
            RunObjectScrubAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.MetadataConsistency(storage.MetadataConsistencyIntervalSeconds),
            RunMetadataConsistencyAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.StorageGarbageCollection(storage.GarbageCollectionIntervalSeconds),
            RunStorageGarbageCollectionAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.MultipartCleanup(storage.MultipartUploadCleanupIntervalMinutes),
            RunMultipartCleanupAsync);
        yield return new ManagedBackgroundTask(
            BackgroundTaskDescriptors.ReplicationWorker(storage.ReplicationIntervalSeconds),
            RunReplicationWorkerAsync);
    }

    private async Task<string?> RefreshClusterNodeAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var registration = CreateRegistration(now);
        await _clusterStore.RegisterNodeAsync(registration, cancellationToken);
        await _clusterStore.HeartbeatNodeAsync(
            new ClusterNodeHeartbeat(
                registration.NodeId,
                registration.Disks.Select(disk => new StorageDiskHeartbeat(
                    disk.DiskId,
                    disk.TotalBytes,
                    disk.AvailableBytes,
                    disk.Status,
                    now)).ToArray(),
                now),
            cancellationToken);
        return $"node={registration.NodeId}; disks={registration.Disks.Count}";
    }

    private async Task<string?> RunDiskHealthIsolationAsync(CancellationToken cancellationToken)
    {
        var changed = await _store.DetectAndIsolateFailedDisksAsync(cancellationToken);
        if (changed > 0)
        {
            _logger.LogInformation("Manual disk health isolation updated {DiskCount} registered disks.", changed);
        }

        return $"changed={changed}";
    }

    private async Task<string?> RunReplicaRepairAsync(CancellationToken cancellationToken)
    {
        var queued = await _store.EnqueueMissingReplicaRepairsAsync(cancellationToken);
        var repaired = await _store.RepairQueuedReplicasAsync(Math.Max(1, _storageOptions.Value.ReplicaRepairBatchSize), cancellationToken);
        if (queued > 0 || repaired > 0)
        {
            _logger.LogInformation("Manual replica repair queued {QueuedCount} items and repaired {RepairedCount} items.", queued, repaired);
        }

        return $"queued={queued}; repaired={repaired}";
    }

    private async Task<string?> RunErasureCodingRepairAsync(CancellationToken cancellationToken)
    {
        var rebuilt = await _store.RebuildErasureCodedObjectsAsync(Math.Max(1, _storageOptions.Value.ReplicaRepairBatchSize), cancellationToken);
        return $"rebuilt={rebuilt}";
    }

    private async Task<string?> RunReplicaRebalanceAsync(CancellationToken cancellationToken)
    {
        var migrated = await _store.RebalanceObjectReplicasAsync(Math.Max(1, _storageOptions.Value.RebalanceBatchSize), cancellationToken);
        return $"migrated={migrated}";
    }

    private async Task<string?> RunLifecycleAsync(CancellationToken cancellationToken)
    {
        var applied = await _store.ApplyLifecycleRulesAsync(
            DateTimeOffset.UtcNow,
            Math.Max(1, _storageOptions.Value.LifecycleBatchSize),
            cancellationToken);
        return $"applied={applied}";
    }

    private async Task<string?> RunObjectScrubAsync(CancellationToken cancellationToken)
    {
        var result = await _store.ScrubObjectReplicasAsync(Math.Max(1, _storageOptions.Value.ScrubBatchSize), cancellationToken);
        return $"checked={result.CheckedReplicas}; missing={result.MissingReplicas}; corrupt={result.CorruptReplicas}; queued={result.QueuedRepairs}";
    }

    private async Task<string?> RunMetadataConsistencyAsync(CancellationToken cancellationToken)
    {
        var result = await _store.CheckMetadataConsistencyAsync(
            repair: true,
            Math.Max(1, _storageOptions.Value.MetadataConsistencyBatchSize),
            cancellationToken);
        return $"checked={result.CheckedObjectCount}; missingVersions={result.MissingCurrentVersionCount}; repairedVersions={result.RepairedCurrentVersionCount}; underReplicated={result.UnderReplicatedObjectCount}; missingReplicaFiles={result.MissingReplicaFileCount}; queued={result.QueuedReplicaRepairCount}; orphanedReplicas={result.OrphanedReplicaRecordCount}";
    }

    private async Task<string?> RunStorageGarbageCollectionAsync(CancellationToken cancellationToken)
    {
        var result = await _store.CollectStorageGarbageAsync(
            delete: true,
            Math.Max(1, _storageOptions.Value.GarbageCollectionBatchSize),
            cancellationToken);
        return $"scanned={result.ScannedFileCount}; candidates={result.CandidateFileCount}; deleted={result.DeletedFileCount}; failedDeletes={result.FailedDeleteCount}; temp={result.ExpiredTempFileCount}; limitReached={result.LimitReached}";
    }

    private async Task<string?> RunMultipartCleanupAsync(CancellationToken cancellationToken)
    {
        var age = TimeSpan.FromHours(Math.Max(1, _storageOptions.Value.MultipartUploadCleanupAgeHours));
        var cutoff = DateTimeOffset.UtcNow.Subtract(age);
        var cleaned = await _store.CleanupMultipartUploadsAsync(cutoff, cancellationToken);
        return $"cleaned={cleaned}; cutoff={cutoff:O}";
    }

    private static Task<string?> RunReplicationWorkerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>("configured=false; replicated=0");
    }

    private ClusterNodeRegistration CreateRegistration(DateTimeOffset now)
    {
        var options = _clusterOptions.Value;
        var disk = ReadObjectDisk(options);
        return new ClusterNodeRegistration(
            Normalize(options.ClusterId, "local"),
            Normalize(options.ClusterName, "Local Means Cluster"),
            Normalize(options.NodeId, Environment.MachineName),
            Environment.MachineName,
            Normalize(options.NodeEndpoint, "http://localhost"),
            Normalize(options.PoolId, "pool-1"),
            Normalize(options.PoolName, "Pool 1"),
            [disk],
            now);
    }

    private StorageDiskRegistration ReadObjectDisk(ClusterOptions options)
    {
        var objectsPath = ResolvePath(_storageOptions.Value.ObjectsPath);
        try
        {
            Directory.CreateDirectory(objectsPath);
            var root = Path.GetPathRoot(objectsPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = objectsPath;
            }

            var drive = new DriveInfo(root);
            return new StorageDiskRegistration(
                Normalize(options.ObjectDiskId, "local-objects"),
                Normalize(options.PoolId, "pool-1"),
                objectsPath,
                Math.Max(0, drive.TotalSize),
                Math.Max(0, drive.AvailableFreeSpace),
                StorageDiskStatuses.Online);
        }
        catch
        {
            return new StorageDiskRegistration(
                Normalize(options.ObjectDiskId, "local-objects"),
                Normalize(options.PoolId, "pool-1"),
                objectsPath,
                0,
                0,
                StorageDiskStatuses.Offline);
        }
    }

    private static string CategoryName(string category)
    {
        return category switch
        {
            "cluster" => "Cluster",
            "repair" => "Repair",
            "rebalance" => "Rebalance",
            "lifecycle" => "Lifecycle",
            "replication" => "Replication",
            _ => category
        };
    }

    private static int CategorySort(string category)
    {
        return category switch
        {
            "cluster" => 0,
            "repair" => 1,
            "rebalance" => 2,
            "lifecycle" => 3,
            "replication" => 4,
            _ => 100
        };
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string Normalize(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record ManagedBackgroundTask(
        BackgroundTaskDescriptor Descriptor,
        Func<CancellationToken, Task<string?>> Operation);
}
