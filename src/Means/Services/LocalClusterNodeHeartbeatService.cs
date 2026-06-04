using Means.Configuration;
using Means.Core;
using Means.Infrastructure.XlFs;
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
    private readonly IOptions<XlFsOptions> _storageOptions;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<LocalClusterNodeHeartbeatService> _logger;
    private readonly BackgroundTaskDescriptor _task;

    public LocalClusterNodeHeartbeatService(
        IClusterStore clusterStore,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<XlFsOptions> storageOptions,
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
        var disks = ReadObjectDisks(options);
        return new ClusterNodeRegistration(
            Normalize(options.ClusterId, "local"),
            Normalize(options.ClusterName, "Local Means Cluster"),
            Normalize(options.NodeId, Environment.MachineName),
            Environment.MachineName,
            Normalize(options.NodeEndpoint, "http://localhost"),
            Normalize(options.PoolId, "pool-1"),
            Normalize(options.PoolName, "Pool 1"),
            disks,
            now);
    }

    private IReadOnlyList<StorageDiskRegistration> ReadObjectDisks(ClusterOptions options)
    {
        var storage = _storageOptions.Value;
        var roots = storage.Disks.Length == 0
            ? [ResolvePath(storage.ObjectsPath)]
            : storage.Disks.Select(ResolvePath).ToArray();
        var disks = new List<StorageDiskRegistration>(roots.Length);
        for (var index = 0; index < roots.Length; index++)
        {
            var path = roots[index];
            var fallbackDiskId = roots.Length == 1 && storage.Disks.Length == 0
                ? Normalize(options.ObjectDiskId, "local-objects")
                : "disk-" + index.ToString("D2");
            try
            {
                Directory.CreateDirectory(path);
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = path;
                }

                var drive = new DriveInfo(root);
                disks.Add(new StorageDiskRegistration(
                    ReadFormattedDiskId(path, fallbackDiskId),
                    Normalize(options.PoolId, "pool-1"),
                    path,
                    Math.Max(0, drive.TotalSize),
                    Math.Max(0, drive.AvailableFreeSpace),
                    StorageDiskStatuses.Online));
            }
            catch
            {
                disks.Add(new StorageDiskRegistration(
                    fallbackDiskId,
                    Normalize(options.PoolId, "pool-1"),
                    path,
                    0,
                    0,
                    StorageDiskStatuses.Offline));
            }
        }

        return disks;
    }

    private static string ReadFormattedDiskId(string rootPath, string fallback)
    {
        var formatPath = Path.Combine(rootPath, ".means.sys", "format.json");
        try
        {
            if (!File.Exists(formatPath))
            {
                return fallback;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(formatPath));
            return document.RootElement.TryGetProperty("diskId", out var diskId)
                && diskId.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(diskId.GetString())
                ? diskId.GetString()!
                : fallback;
        }
        catch
        {
            return fallback;
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
