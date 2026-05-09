using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<int> DetectAndIsolateFailedDisksAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var disks = await ListRegisteredDisksForHealthAsync(cancellationToken);
        var changed = 0;
        foreach (var disk in disks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var health = ProbeDisk(disk.MountPath);
            var nextStatus = health.Online ? StorageDiskStatuses.Online : StorageDiskStatuses.Offline;
            if (string.Equals(disk.Status, nextStatus, StringComparison.Ordinal))
            {
                continue;
            }

            await UpdateDiskHealthAsync(disk.NodeId, disk.DiskId, nextStatus, health.TotalBytes, health.AvailableBytes, cancellationToken);
            changed++;
        }

        return changed;
    }

    private async Task<IReadOnlyList<DiskHealthTarget>> ListRegisteredDisksForHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select node_id, disk_id, mount_path, status
            from storage_disks
            order by node_id, disk_id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var disks = new List<DiskHealthTarget>();
        while (await reader.ReadAsync(cancellationToken))
        {
            disks.Add(new DiskHealthTarget(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return disks;
    }

    private async Task UpdateDiskHealthAsync(
        string nodeId,
        string diskId,
        string status,
        long totalBytes,
        long availableBytes,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update storage_disks
            set status = $status,
                total_bytes = case when $total >= 0 then $total else total_bytes end,
                available_bytes = case when $available >= 0 then $available else available_bytes end,
                last_seen_utc = case when $status = $online then $now else last_seen_utc end
            where node_id = $nodeId and disk_id = $diskId;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$total", totalBytes);
        command.Parameters.AddWithValue("$available", availableBytes);
        command.Parameters.AddWithValue("$online", StorageDiskStatuses.Online);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$nodeId", nodeId);
        command.Parameters.AddWithValue("$diskId", diskId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DiskProbeResult ProbeDisk(string mountPath)
    {
        try
        {
            if (!Directory.Exists(mountPath))
            {
                return new DiskProbeResult(false, -1, -1);
            }

            var healthFile = Path.Combine(mountPath, ".means-disk-health-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(healthFile, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(healthFile);

            var root = Path.GetPathRoot(mountPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = mountPath;
            }

            var drive = new DriveInfo(root);
            return new DiskProbeResult(true, Math.Max(0, drive.TotalSize), Math.Max(0, drive.AvailableFreeSpace));
        }
        catch
        {
            return new DiskProbeResult(false, -1, -1);
        }
    }

    private sealed record DiskHealthTarget(string NodeId, string DiskId, string MountPath, string Status);

    private sealed record DiskProbeResult(bool Online, long TotalBytes, long AvailableBytes);
}
