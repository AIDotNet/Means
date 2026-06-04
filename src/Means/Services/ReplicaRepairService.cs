using Means.Core;
using Means.Infrastructure.XlFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Periodically scans object replica manifests and repairs missing local replica files.
/// Cross-node repair can reuse the same queue once remote replica fetch is implemented.
/// </summary>
public sealed class ReplicaRepairService : BackgroundService
{
    private readonly IStorageMaintenanceOperations _store;
    private readonly IOptions<XlFsOptions> _options;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<ReplicaRepairService> _logger;
    private readonly BackgroundTaskDescriptor _task;

    public ReplicaRepairService(
        IStorageMaintenanceOperations store,
        IOptions<XlFsOptions> options,
        IBackgroundTaskRegistry backgroundTasks,
        ILogger<ReplicaRepairService> logger)
    {
        _store = store;
        _options = options;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _task = BackgroundTaskDescriptors.ReplicaRepair(_options.Value.ReplicaRepairIntervalSeconds);
        _backgroundTasks.Register(_task);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RepairOnceAsync(stoppingToken);

            var interval = TimeSpan.FromSeconds(Math.Max(5, _options.Value.ReplicaRepairIntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RepairOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _backgroundTasks.RunAsync(
                _task,
                async token =>
                {
                    var queued = await _store.EnqueueMissingReplicaRepairsAsync(token);
                    var repaired = await _store.RepairQueuedReplicasAsync(Math.Max(1, _options.Value.ReplicaRepairBatchSize), token);
                    if (queued > 0 || repaired > 0)
                    {
                        _logger.LogInformation(
                            "Replica repair pass queued {QueuedCount} items and repaired {RepairedCount} items with concurrency {MaxConcurrency}.",
                            queued,
                            repaired,
                            Math.Max(1, _options.Value.ReplicaRepairMaxConcurrency));
                    }

                    return $"queued={queued}; repaired={repaired}; concurrency={Math.Max(1, _options.Value.ReplicaRepairMaxConcurrency)}; throttleMs={Math.Max(0, _options.Value.ReplicaRepairThrottleDelayMilliseconds)}";
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replica repair pass failed.");
        }
    }
}
