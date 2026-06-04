using Means.Core;
using Means.Infrastructure.XlFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Runs low-priority storage maintenance that can move bytes without affecting foreground writes.
/// Each pass stages data first, then commits compact MeansLogDb mutations atomically.
/// </summary>
public sealed class StorageMaintenanceService : BackgroundService
{
    private readonly IStorageMaintenanceOperations _store;
    private readonly IOptions<XlFsOptions> _options;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly BackgroundTaskDescriptor _ecRepairTask;
    private readonly BackgroundTaskDescriptor _rebalanceTask;
    private readonly BackgroundTaskDescriptor _lifecycleTask;
    private readonly BackgroundTaskDescriptor _scrubTask;
    private readonly BackgroundTaskDescriptor _metadataConsistencyTask;
    private readonly BackgroundTaskDescriptor _garbageCollectionTask;

    public StorageMaintenanceService(
        IStorageMaintenanceOperations store,
        IOptions<XlFsOptions> options,
        IBackgroundTaskRegistry backgroundTasks,
        ILogger<StorageMaintenanceService> logger)
    {
        _store = store;
        _options = options;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _ecRepairTask = BackgroundTaskDescriptors.ErasureCodingRepair(_options.Value.ErasureCodingRepairIntervalSeconds);
        _rebalanceTask = BackgroundTaskDescriptors.ReplicaRebalance(_options.Value.RebalanceIntervalSeconds);
        _lifecycleTask = BackgroundTaskDescriptors.Lifecycle(_options.Value.LifecycleIntervalSeconds);
        _scrubTask = BackgroundTaskDescriptors.ObjectScrub(_options.Value.ScrubIntervalSeconds);
        _metadataConsistencyTask = BackgroundTaskDescriptors.MetadataConsistency(_options.Value.MetadataConsistencyIntervalSeconds);
        _garbageCollectionTask = BackgroundTaskDescriptors.StorageGarbageCollection(_options.Value.GarbageCollectionIntervalSeconds);
        _backgroundTasks.Register(_ecRepairTask);
        _backgroundTasks.Register(_rebalanceTask);
        _backgroundTasks.Register(_lifecycleTask);
        _backgroundTasks.Register(_scrubTask);
        _backgroundTasks.Register(_metadataConsistencyTask);
        _backgroundTasks.Register(_garbageCollectionTask);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            var intervalSeconds = Math.Min(
                Math.Min(
                    Math.Max(5, _options.Value.ErasureCodingRepairIntervalSeconds),
                    Math.Max(5, _options.Value.RebalanceIntervalSeconds)),
                Math.Min(
                    Math.Max(5, _options.Value.LifecycleIntervalSeconds),
                    Math.Min(
                        Math.Max(5, _options.Value.ScrubIntervalSeconds),
                        Math.Min(
                            Math.Max(5, _options.Value.MetadataConsistencyIntervalSeconds),
                            Math.Max(5, _options.Value.GarbageCollectionIntervalSeconds)))));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunTaskAsync(
            _ecRepairTask,
            async token =>
            {
                var rebuilt = await _store.RebuildErasureCodedObjectsAsync(Math.Max(1, _options.Value.ReplicaRepairBatchSize), token);
                return $"rebuilt={rebuilt}";
            },
            cancellationToken);

        await RunTaskAsync(
            _rebalanceTask,
            async token =>
            {
                var migrated = await _store.RebalanceObjectReplicasAsync(Math.Max(1, _options.Value.RebalanceBatchSize), token);
                return $"migrated={migrated}";
            },
            cancellationToken);

        await RunTaskAsync(
            _lifecycleTask,
            async token =>
            {
                var applied = await _store.ApplyLifecycleRulesAsync(DateTimeOffset.UtcNow, Math.Max(1, _options.Value.LifecycleBatchSize), token);
                return $"applied={applied}";
            },
            cancellationToken);

        await RunTaskAsync(
            _scrubTask,
            async token =>
            {
                var result = await _store.ScrubObjectReplicasAsync(Math.Max(1, _options.Value.ScrubBatchSize), token);
                return $"checked={result.CheckedReplicas}; missing={result.MissingReplicas}; corrupt={result.CorruptReplicas}; queued={result.QueuedRepairs}";
            },
            cancellationToken);

        await RunTaskAsync(
            _metadataConsistencyTask,
            async token =>
            {
                var result = await _store.CheckMetadataConsistencyAsync(
                    repair: true,
                    Math.Max(1, _options.Value.MetadataConsistencyBatchSize),
                    token);
                return $"checked={result.CheckedObjectCount}; missingVersions={result.MissingCurrentVersionCount}; repairedVersions={result.RepairedCurrentVersionCount}; underReplicated={result.UnderReplicatedObjectCount}; missingReplicaFiles={result.MissingReplicaFileCount}; queued={result.QueuedReplicaRepairCount}; orphanedReplicas={result.OrphanedReplicaRecordCount}";
            },
            cancellationToken);

        await RunTaskAsync(
            _garbageCollectionTask,
            async token =>
            {
                var result = await _store.CollectStorageGarbageAsync(
                    delete: true,
                    Math.Max(1, _options.Value.GarbageCollectionBatchSize),
                    token);
                return $"scanned={result.ScannedFileCount}; candidates={result.CandidateFileCount}; deleted={result.DeletedFileCount}; failedDeletes={result.FailedDeleteCount}; temp={result.ExpiredTempFileCount}; limitReached={result.LimitReached}";
            },
            cancellationToken);
    }

    private async Task RunTaskAsync(
        BackgroundTaskDescriptor descriptor,
        Func<CancellationToken, Task<string?>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _backgroundTasks.RunAsync(descriptor, operation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storage maintenance task {TaskId} failed.", descriptor.TaskId);
        }
    }
}
