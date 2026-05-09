using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<ClusterDiagnostics> GetClusterDiagnosticsAsync(
        DateTimeOffset offlineBeforeUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var topology = await GetClusterTopologyAsync(offlineBeforeUtc, cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var storage = await ReadDiagnosticsStorageMetricsAsync(connection, cancellationToken);
        var objectReplicas = await ReadObjectReplicaDiagnosticsAsync(connection, cancellationToken);
        var repairQueue = await ReadReplicaRepairQueueDiagnosticsAsync(connection, cancellationToken);
        var metadata = await ReadMetadataDiagnosticsAsync(connection, cancellationToken);
        var erasureCoding = await ReadErasureCodingDiagnosticsAsync(connection, cancellationToken);
        var summary = BuildClusterDiagnosticsSummary(topology, storage);

        return new ClusterDiagnostics(
            DateTimeOffset.UtcNow,
            summary,
            topology,
            objectReplicas,
            repairQueue,
            metadata,
            erasureCoding,
            Array.Empty<BackgroundTaskSnapshot>());
    }

    private static async Task<ConsoleStorageMetrics> ReadDiagnosticsStorageMetricsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                (select count(*) from buckets),
                (select count(*) from objects),
                coalesce((select sum(content_length) from objects), 0);
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ConsoleStorageMetrics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private async Task<ObjectReplicaDiagnostics> ReadObjectReplicaDiagnosticsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var desiredReplicaCount = Math.Clamp(_options.ReplicaCount, 1, 16);
        var replicaCounts = await ReadReplicaCountsByObjectAsync(connection, cancellationToken);
        var replicas = await ReadReplicaFileProbesAsync(connection, cancellationToken);

        var existingReplicaFiles = 0L;
        var missingReplicaFiles = 0L;
        var missingReplicaObjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var replica in replicas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(replica.ContentPath))
            {
                existingReplicaFiles++;
                continue;
            }

            missingReplicaFiles++;
            missingReplicaObjects.Add(replica.ObjectId);
        }

        return new ObjectReplicaDiagnostics(
            desiredReplicaCount,
            replicaCounts.Count,
            replicas.Count,
            replicas.LongCount(replica => string.Equals(replica.Status, ObjectReplicaStatusCommitted, StringComparison.Ordinal)),
            existingReplicaFiles,
            missingReplicaFiles,
            missingReplicaObjects.Count,
            replicaCounts.LongCount(count => count < desiredReplicaCount),
            replicaCounts.LongCount(count => count == 0));
    }

    private static async Task<IReadOnlyList<int>> ReadReplicaCountsByObjectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(r.object_id)
            from objects o
            left join object_replicas r on r.object_id = o.object_id
            group by o.object_id
            order by o.object_id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(Convert.ToInt32(reader.GetInt64(0)));
        }

        return counts;
    }

    private static async Task<IReadOnlyList<ObjectReplicaFileProbe>> ReadReplicaFileProbesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, content_path, status
            from object_replicas
            order by object_id, replica_index;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var replicas = new List<ObjectReplicaFileProbe>();
        while (await reader.ReadAsync(cancellationToken))
        {
            replicas.Add(new ObjectReplicaFileProbe(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return replicas;
    }

    private async Task<ReplicaRepairQueueDiagnostics> ReadReplicaRepairQueueDiagnosticsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select status, attempts, created_utc, updated_utc
            from object_replica_repairs
            order by updated_utc, object_id;
            """;

        var total = 0L;
        var pending = 0L;
        var completed = 0L;
        var failed = 0L;
        var retryableFailed = 0L;
        var maxAttemptsReached = 0L;
        DateTimeOffset? oldestPending = null;
        DateTimeOffset? lastUpdated = null;
        var statusCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var maxAttempts = Math.Max(1, _options.ReplicaRepairMaxAttempts);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total++;
            var status = reader.GetString(0);
            var attempts = reader.GetInt32(1);
            var createdAt = DateTimeOffset.Parse(reader.GetString(2));
            var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

            statusCounts.TryGetValue(status, out var statusCount);
            statusCounts[status] = statusCount + 1;

            if (string.Equals(status, ReplicaRepairStatusPending, StringComparison.Ordinal))
            {
                pending++;
                oldestPending = oldestPending is null || createdAt < oldestPending.Value ? createdAt : oldestPending;
            }
            else if (string.Equals(status, ReplicaRepairStatusCompleted, StringComparison.Ordinal))
            {
                completed++;
            }
            else if (string.Equals(status, ReplicaRepairStatusFailed, StringComparison.Ordinal))
            {
                failed++;
                if (attempts < maxAttempts)
                {
                    retryableFailed++;
                }
                else
                {
                    maxAttemptsReached++;
                }
            }

            lastUpdated = lastUpdated is null || updatedAt > lastUpdated.Value ? updatedAt : lastUpdated;
        }

        return new ReplicaRepairQueueDiagnostics(
            total,
            pending,
            completed,
            failed,
            retryableFailed,
            maxAttemptsReached,
            oldestPending,
            lastUpdated,
            statusCounts.Select(pair => new ReplicaRepairQueueStatusDiagnostics(pair.Key, pair.Value)).ToArray());
    }

    private static async Task<ErasureCodingDiagnostics> ReadErasureCodingDiagnosticsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                count(*),
                coalesce(sum(case when enabled = 1 then 1 else 0 end), 0)
            from erasure_coding_profiles;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var profiles = reader.GetInt64(0);
        var enabled = reader.GetInt64(1);
        return new ErasureCodingDiagnostics(profiles, enabled, profiles - enabled);
    }

    private static async Task<MetadataDiagnostics> ReadMetadataDiagnosticsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return new MetadataDiagnostics(
            await CountPendingMetadataCommitsAsync(connection, cancellationToken),
            await CountOrphanedReplicaRecordsAsync(connection, cancellationToken));
    }

    private static ClusterDiagnosticsSummary BuildClusterDiagnosticsSummary(
        ClusterTopology topology,
        ConsoleStorageMetrics storage)
    {
        var onlineNodes = topology.Nodes.Count(node => node.Status == ClusterNodeStatuses.Online);
        var disks = topology.Nodes.SelectMany(node => node.Disks).ToArray();
        var onlineDisks = disks.Count(disk => disk.Status == StorageDiskStatuses.Online);
        var totalCapacity = topology.Pools.Sum(pool => pool.TotalBytes);
        var availableCapacity = topology.Pools.Sum(pool => pool.AvailableBytes);

        return new ClusterDiagnosticsSummary(
            storage.BucketCount,
            storage.ObjectCount,
            storage.TotalBytes,
            topology.Nodes.Count,
            onlineNodes,
            topology.Nodes.Count - onlineNodes,
            topology.Pools.Count,
            disks.Length,
            onlineDisks,
            disks.Length - onlineDisks,
            totalCapacity,
            availableCapacity,
            Math.Max(0, totalCapacity - availableCapacity));
    }

    private sealed record ObjectReplicaFileProbe(string ObjectId, string ContentPath, string Status);
}
