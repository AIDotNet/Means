using Means.Configuration;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Detects inaccessible registered disks and isolates them from placement by marking them offline.
/// A later healthy probe can mark the disk online again.
/// </summary>
public sealed class DiskHealthIsolationService : BackgroundService
{
    private readonly IStorageMaintenanceOperations _store;
    private readonly IOptions<ClusterOptions> _options;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<DiskHealthIsolationService> _logger;
    private readonly BackgroundTaskDescriptor _task;

    public DiskHealthIsolationService(
        IStorageMaintenanceOperations store,
        IOptions<ClusterOptions> options,
        IBackgroundTaskRegistry backgroundTasks,
        ILogger<DiskHealthIsolationService> logger)
    {
        _store = store;
        _options = options;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _task = BackgroundTaskDescriptors.DiskHealthIsolation(_options.Value.DiskHealthIntervalSeconds);
        _backgroundTasks.Register(_task);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckOnceAsync(stoppingToken);

            var interval = TimeSpan.FromSeconds(Math.Max(5, _options.Value.DiskHealthIntervalSeconds));
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

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _backgroundTasks.RunAsync(
                _task,
                async token =>
                {
                    var changed = await _store.DetectAndIsolateFailedDisksAsync(token);
                    if (changed > 0)
                    {
                        _logger.LogInformation("Disk health isolation updated {DiskCount} registered disks.", changed);
                    }

                    return $"changed={changed}";
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk health isolation pass failed.");
        }
    }
}
