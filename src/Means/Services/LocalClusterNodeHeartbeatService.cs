using Means.Configuration;
using Means.Core;
using Means.Infrastructure.SqliteFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Registers the current process as a storage node and refreshes its disk heartbeat.
/// This is still a single-node heartbeat, but it establishes the control-plane contract
/// required by later placement, repair, and rebalance workers.
/// </summary>
public sealed class LocalClusterNodeHeartbeatService : BackgroundService
{
    private readonly IClusterStore _clusterStore;
    private readonly IOptions<ClusterOptions> _clusterOptions;
    private readonly IOptions<SqliteFsOptions> _storageOptions;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<LocalClusterNodeHeartbeatService> _logger;
    private readonly BackgroundTaskDescriptor _task;

    public LocalClusterNodeHeartbeatService(
        IClusterStore clusterStore,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<SqliteFsOptions> storageOptions,
        IBackgroundTaskRegistry backgroundTasks,
        ILogger<LocalClusterNodeHeartbeatService> logger)
    {
        _clusterStore = clusterStore;
        _clusterOptions = clusterOptions;
        _storageOptions = storageOptions;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _task = BackgroundTaskDescriptors.ClusterHeartbeat(_clusterOptions.Value.HeartbeatIntervalSeconds);
        _backgroundTasks.Register(_task);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshNodeAsync(stoppingToken);

            var interval = TimeSpan.FromSeconds(Math.Max(5, _clusterOptions.Value.HeartbeatIntervalSeconds));
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

    private async Task RefreshNodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _backgroundTasks.RunAsync(
                _task,
                async token =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var registration = CreateRegistration(now);
                    await _clusterStore.RegisterNodeAsync(registration, token);
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
                        token);
                    return $"node={registration.NodeId}; disks={registration.Disks.Count}";
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh local cluster node heartbeat.");
        }
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

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string Normalize(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
