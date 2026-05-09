using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task RegisterNodeAsync(ClusterNodeRegistration registration, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        await EnsureInitializedAsync(cancellationToken);

        var registeredAt = registration.RegisteredAt.ToUniversalTime();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clusterCommand = connection.CreateCommand())
        {
            clusterCommand.Transaction = (SqliteTransaction)transaction;
            clusterCommand.CommandText = """
                insert into storage_clusters(cluster_id, name, created_utc)
                values($clusterId, $name, $created)
                on conflict(cluster_id) do update set
                    name = excluded.name;
                """;
            clusterCommand.Parameters.AddWithValue("$clusterId", registration.ClusterId);
            clusterCommand.Parameters.AddWithValue("$name", registration.ClusterName);
            clusterCommand.Parameters.AddWithValue("$created", registeredAt.ToString("O"));
            await clusterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var poolCommand = connection.CreateCommand())
        {
            poolCommand.Transaction = (SqliteTransaction)transaction;
            poolCommand.CommandText = """
                insert into storage_pools(pool_id, cluster_id, name, created_utc)
                values($poolId, $clusterId, $name, $created)
                on conflict(pool_id) do update set
                    cluster_id = excluded.cluster_id,
                    name = excluded.name;
                """;
            poolCommand.Parameters.AddWithValue("$poolId", registration.PoolId);
            poolCommand.Parameters.AddWithValue("$clusterId", registration.ClusterId);
            poolCommand.Parameters.AddWithValue("$name", registration.PoolName);
            poolCommand.Parameters.AddWithValue("$created", registeredAt.ToString("O"));
            await poolCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.Transaction = (SqliteTransaction)transaction;
            nodeCommand.CommandText = """
                insert into storage_nodes(
                    node_id,
                    cluster_id,
                    host_name,
                    endpoint,
                    status,
                    registered_utc,
                    last_heartbeat_utc)
                values(
                    $nodeId,
                    $clusterId,
                    $hostName,
                    $endpoint,
                    $status,
                    $registered,
                    $heartbeat)
                on conflict(node_id) do update set
                    cluster_id = excluded.cluster_id,
                    host_name = excluded.host_name,
                    endpoint = excluded.endpoint,
                    status = excluded.status,
                    last_heartbeat_utc = excluded.last_heartbeat_utc;
                """;
            nodeCommand.Parameters.AddWithValue("$nodeId", registration.NodeId);
            nodeCommand.Parameters.AddWithValue("$clusterId", registration.ClusterId);
            nodeCommand.Parameters.AddWithValue("$hostName", registration.HostName);
            nodeCommand.Parameters.AddWithValue("$endpoint", registration.Endpoint);
            nodeCommand.Parameters.AddWithValue("$status", ClusterNodeStatuses.Online);
            nodeCommand.Parameters.AddWithValue("$registered", registeredAt.ToString("O"));
            nodeCommand.Parameters.AddWithValue("$heartbeat", registeredAt.ToString("O"));
            await nodeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var disk in registration.Disks)
        {
            await UpsertDiskAsync(connection, (SqliteTransaction)transaction, registration.NodeId, disk, registeredAt, cancellationToken);
        }

        await MarkMissingDisksOfflineAsync(
            connection,
            (SqliteTransaction)transaction,
            registration.NodeId,
            registration.Disks.Select(disk => disk.DiskId).ToArray(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task HeartbeatNodeAsync(ClusterNodeHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(heartbeat.NodeId))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Node id is required.", 400);
        }

        await EnsureInitializedAsync(cancellationToken);

        var heartbeatAt = heartbeat.HeartbeatAt.ToUniversalTime();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.Transaction = (SqliteTransaction)transaction;
            nodeCommand.CommandText = """
                update storage_nodes
                set status = $status,
                    last_heartbeat_utc = $heartbeat
                where node_id = $nodeId;
                """;
            nodeCommand.Parameters.AddWithValue("$status", ClusterNodeStatuses.Online);
            nodeCommand.Parameters.AddWithValue("$heartbeat", heartbeatAt.ToString("O"));
            nodeCommand.Parameters.AddWithValue("$nodeId", heartbeat.NodeId);
            var updated = await nodeCommand.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, "Node is not registered.", 404);
            }
        }

        foreach (var disk in heartbeat.Disks)
        {
            await using var diskCommand = connection.CreateCommand();
            diskCommand.Transaction = (SqliteTransaction)transaction;
            diskCommand.CommandText = """
                update storage_disks
                set total_bytes = $total,
                    available_bytes = $available,
                    status = $status,
                    last_seen_utc = $lastSeen
                where node_id = $nodeId and disk_id = $diskId;
                """;
            diskCommand.Parameters.AddWithValue("$total", Math.Max(0, disk.TotalBytes));
            diskCommand.Parameters.AddWithValue("$available", Math.Max(0, disk.AvailableBytes));
            diskCommand.Parameters.AddWithValue("$status", NormalizeDiskStatus(disk.Status));
            diskCommand.Parameters.AddWithValue("$lastSeen", disk.LastSeenAt.ToUniversalTime().ToString("O"));
            diskCommand.Parameters.AddWithValue("$nodeId", heartbeat.NodeId);
            diskCommand.Parameters.AddWithValue("$diskId", disk.DiskId);
            await diskCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkMissingDisksOfflineAsync(
            connection,
            (SqliteTransaction)transaction,
            heartbeat.NodeId,
            heartbeat.Disks.Select(disk => disk.DiskId).ToArray(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ClusterTopology> GetClusterTopologyAsync(DateTimeOffset offlineBeforeUtc, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var cluster = await ReadClusterAsync(connection, cancellationToken);
        if (cluster is null)
        {
            return new ClusterTopology(
                new StorageClusterInfo("unregistered", "Unregistered", DateTimeOffset.UnixEpoch),
                Array.Empty<ClusterNodeInfo>(),
                Array.Empty<StoragePoolInfo>());
        }

        var cutoffUtc = offlineBeforeUtc.ToUniversalTime();
        var disksByNode = await ReadDisksByNodeAsync(connection, cluster.ClusterId, cutoffUtc, cancellationToken);
        var nodes = await ReadNodesAsync(connection, cluster.ClusterId, disksByNode, cutoffUtc, cancellationToken);
        var pools = await ReadPoolsAsync(connection, cluster.ClusterId, cutoffUtc, cancellationToken);
        return new ClusterTopology(cluster, nodes, pools);
    }

    private static async Task UpsertDiskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        StorageDiskRegistration disk,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var diskCommand = connection.CreateCommand();
        diskCommand.Transaction = transaction;
        diskCommand.CommandText = """
            insert into storage_disks(
                node_id,
                disk_id,
                pool_id,
                mount_path,
                total_bytes,
                available_bytes,
                status,
                last_seen_utc)
            values(
                $nodeId,
                $diskId,
                $poolId,
                $mountPath,
                $total,
                $available,
                $status,
                $lastSeen)
            on conflict(node_id, disk_id) do update set
                pool_id = excluded.pool_id,
                mount_path = excluded.mount_path,
                total_bytes = excluded.total_bytes,
                available_bytes = excluded.available_bytes,
                status = excluded.status,
                last_seen_utc = excluded.last_seen_utc;
            """;
        diskCommand.Parameters.AddWithValue("$nodeId", nodeId);
        diskCommand.Parameters.AddWithValue("$diskId", disk.DiskId);
        diskCommand.Parameters.AddWithValue("$poolId", disk.PoolId);
        diskCommand.Parameters.AddWithValue("$mountPath", disk.MountPath);
        diskCommand.Parameters.AddWithValue("$total", Math.Max(0, disk.TotalBytes));
        diskCommand.Parameters.AddWithValue("$available", Math.Max(0, disk.AvailableBytes));
        diskCommand.Parameters.AddWithValue("$status", NormalizeDiskStatus(disk.Status));
        diskCommand.Parameters.AddWithValue("$lastSeen", now.ToString("O"));
        await diskCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMissingDisksOfflineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        IReadOnlyList<string> reportedDiskIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$nodeId", nodeId);
        command.Parameters.AddWithValue("$offline", StorageDiskStatuses.Offline);

        if (reportedDiskIds.Count == 0)
        {
            command.CommandText = """
                update storage_disks
                set status = $offline
                where node_id = $nodeId;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var placeholders = new string[reportedDiskIds.Count];
        for (var index = 0; index < reportedDiskIds.Count; index++)
        {
            var parameterName = "$disk" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, reportedDiskIds[index]);
        }

        command.CommandText = $"""
            update storage_disks
            set status = $offline
            where node_id = $nodeId
              and disk_id not in ({string.Join(", ", placeholders)});
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StorageClusterInfo?> ReadClusterAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select cluster_id, name, created_utc
            from storage_clusters
            order by created_utc
            limit 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StorageClusterInfo(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<StorageDiskInfo>>> ReadDisksByNodeAsync(
        SqliteConnection connection,
        string clusterId,
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                d.disk_id,
                d.node_id,
                d.pool_id,
                d.mount_path,
                d.total_bytes,
                d.available_bytes,
                d.status,
                d.last_seen_utc,
                n.last_heartbeat_utc
            from storage_disks d
            join storage_nodes n on n.node_id = d.node_id
            where n.cluster_id = $clusterId
            order by d.node_id, d.disk_id;
            """;
        command.Parameters.AddWithValue("$clusterId", clusterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var disks = new Dictionary<string, List<StorageDiskInfo>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(1);
            var diskLastSeen = DateTimeOffset.Parse(reader.GetString(7));
            var lastHeartbeat = DateTimeOffset.Parse(reader.GetString(8));
            var diskStatus = lastHeartbeat < offlineBeforeUtc || diskLastSeen < offlineBeforeUtc
                ? StorageDiskStatuses.Offline
                : NormalizeDiskStatus(reader.GetString(6));
            if (!disks.TryGetValue(nodeId, out var nodeDisks))
            {
                nodeDisks = [];
                disks[nodeId] = nodeDisks;
            }

            nodeDisks.Add(new StorageDiskInfo(
                reader.GetString(0),
                nodeId,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                diskStatus,
                diskLastSeen));
        }

        return disks.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<StorageDiskInfo>)pair.Value, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<ClusterNodeInfo>> ReadNodesAsync(
        SqliteConnection connection,
        string clusterId,
        IReadOnlyDictionary<string, IReadOnlyList<StorageDiskInfo>> disksByNode,
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select node_id, cluster_id, host_name, endpoint, status, registered_utc, last_heartbeat_utc
            from storage_nodes
            where cluster_id = $clusterId
            order by node_id;
            """;
        command.Parameters.AddWithValue("$clusterId", clusterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<ClusterNodeInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(0);
            var lastHeartbeat = DateTimeOffset.Parse(reader.GetString(6));
            var nodeStatus = lastHeartbeat < offlineBeforeUtc
                ? ClusterNodeStatuses.Offline
                : NormalizeNodeStatus(reader.GetString(4));
            nodes.Add(new ClusterNodeInfo(
                nodeId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                nodeStatus,
                DateTimeOffset.Parse(reader.GetString(5)),
                lastHeartbeat,
                disksByNode.TryGetValue(nodeId, out var disks) ? disks : Array.Empty<StorageDiskInfo>()));
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<StoragePoolInfo>> ReadPoolsAsync(
        SqliteConnection connection,
        string clusterId,
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                p.pool_id,
                p.cluster_id,
                p.name,
                p.created_utc,
                count(distinct d.node_id),
                count(d.disk_id),
                coalesce(sum(case
                    when d.status = $online
                     and n.status = $online
                     and d.last_seen_utc >= $offlineBefore
                     and n.last_heartbeat_utc >= $offlineBefore
                    then d.total_bytes
                    else 0
                end), 0),
                coalesce(sum(case
                    when d.status = $online
                     and n.status = $online
                     and d.last_seen_utc >= $offlineBefore
                     and n.last_heartbeat_utc >= $offlineBefore
                    then d.available_bytes
                    else 0
                end), 0)
            from storage_pools p
            left join storage_disks d on d.pool_id = p.pool_id
            left join storage_nodes n on n.node_id = d.node_id
            where p.cluster_id = $clusterId
            group by p.pool_id, p.cluster_id, p.name, p.created_utc
            order by p.pool_id;
            """;
        command.Parameters.AddWithValue("$clusterId", clusterId);
        command.Parameters.AddWithValue("$online", StorageDiskStatuses.Online);
        command.Parameters.AddWithValue("$offlineBefore", offlineBeforeUtc.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pools = new List<StoragePoolInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            pools.Add(new StoragePoolInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                Convert.ToInt32(reader.GetInt64(4)),
                Convert.ToInt32(reader.GetInt64(5)),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return pools;
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
}
