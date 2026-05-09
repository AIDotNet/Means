using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string RebalanceTaskStatusCompleted = "Completed";
    private const string RebalanceTaskStatusFailed = "Failed";

    public async Task<int> RebalanceObjectReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var candidates = await ReadReplicaRebalanceCandidatesAsync(Math.Clamp(maxItems, 1, 1000), cancellationToken);
        var migrated = 0;
        foreach (var candidate in candidates)
        {
            if (await TryMigrateReplicaAsync(candidate, cancellationToken))
            {
                migrated++;
            }
        }

        return migrated;
    }

    private async Task<IReadOnlyList<ReplicaRebalanceCandidate>> ReadReplicaRebalanceCandidatesAsync(
        int maxItems,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow
            .Subtract(TimeSpan.FromSeconds(Math.Max(5, _options.ReplicaOfflineAfterSeconds)))
            .ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                r.object_id,
                o.bucket_name,
                o.key,
                o.content_length,
                r.replica_index,
                r.content_path
            from object_replicas r
            join objects o on o.object_id = r.object_id
            left join storage_nodes n on n.node_id = r.node_id
            left join storage_disks d on d.node_id = r.node_id and d.disk_id = r.disk_id
            where n.node_id is null
               or d.disk_id is null
               or n.status <> $online
               or d.status <> $online
               or n.last_heartbeat_utc < $cutoff
               or d.last_seen_utc < $cutoff
            order by o.bucket_name, o.key, r.replica_index
            limit $limit;
            """;
        command.Parameters.AddWithValue("$online", ClusterNodeStatuses.Online);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", maxItems);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<ReplicaRebalanceCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new ReplicaRebalanceCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }

        return candidates;
    }

    private async Task<bool> TryMigrateReplicaAsync(ReplicaRebalanceCandidate candidate, CancellationToken cancellationToken)
    {
        var taskId = Guid.NewGuid().ToString("N");
        string? createdPath = null;
        string? tempSourcePath = null;
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var currentObjectId = await GetObjectIdAsync(connection, candidate.BucketName, candidate.Key, cancellationToken);
            if (!string.Equals(currentObjectId, candidate.ObjectId, StringComparison.Ordinal))
            {
                return false;
            }

            var replicas = await GetObjectReplicasAsync(connection, candidate.ObjectId, cancellationToken);
            var sourcePath = await FindReadableReplicaPathAsync(candidate.ObjectId, replicas, cancellationToken);
            if (sourcePath is null)
            {
                Directory.CreateDirectory(Path.Combine(_options.ObjectsPath, "tmp"));
                tempSourcePath = Path.Combine(_options.ObjectsPath, "tmp", Guid.NewGuid().ToString("N") + ".rebalance.tmp");
                if (!await TryReconstructErasureCodedObjectAsync(candidate.ObjectId, tempSourcePath, cancellationToken))
                {
                    throw new InvalidOperationException("No readable source is available for replica rebalance.");
                }

                sourcePath = tempSourcePath;
            }

            var target = await SelectReplicaRebalanceTargetAsync(candidate, replicas, cancellationToken);
            if (target is null)
            {
                return false;
            }

            createdPath = GetObjectPath(target.MountPath, candidate.ObjectId);
            if (string.Equals(Path.GetFullPath(createdPath), Path.GetFullPath(candidate.ContentPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(createdPath)!);
            File.Copy(sourcePath, createdPath, overwrite: false);
            var checksum = await ComputeFileSha256Async(createdPath, cancellationToken);
            var replacement = new ObjectReplicaRecord(
                candidate.ObjectId,
                candidate.ReplicaIndex,
                target.NodeId,
                target.DiskId,
                target.PoolId,
                createdPath,
                ObjectReplicaStatusCommitted,
                checksum,
                DateTimeOffset.UtcNow);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await UpsertReplicaForRebalanceAsync(connection, (SqliteTransaction)transaction, replacement, cancellationToken);
                await InsertRebalanceTaskAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    taskId,
                    candidate.ObjectId,
                    null,
                    candidate.ContentPath,
                    createdPath,
                    "ReplicaOfflinePlacement",
                    RebalanceTaskStatusCompleted,
                    null,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                DeleteFileQuietly(createdPath);
                throw;
            }

            if (!string.Equals(Path.GetFullPath(candidate.ContentPath), Path.GetFullPath(createdPath), StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileQuietly(candidate.ContentPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (createdPath is not null)
            {
                DeleteFileQuietly(createdPath);
            }

            await RecordFailedRebalanceTaskAsync(taskId, candidate, ex.Message, cancellationToken);
            return false;
        }
        finally
        {
            if (tempSourcePath is not null)
            {
                DeleteFileQuietly(tempSourcePath);
            }
        }
    }

    private async Task<ObjectPlacementReplica?> SelectReplicaRebalanceTargetAsync(
        ReplicaRebalanceCandidate candidate,
        IReadOnlyList<ObjectReplicaRecord> existingReplicas,
        CancellationToken cancellationToken)
    {
        var existingPaths = existingReplicas
            .Where(replica => replica.ReplicaIndex != candidate.ReplicaIndex)
            .Select(replica => Path.GetFullPath(replica.ContentPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = await PlanObjectReplicasAsync(
            candidate.BucketName,
            candidate.Key,
            candidate.ObjectId,
            candidate.ContentLength,
            cancellationToken);

        foreach (var replica in plan.Replicas
            .OrderBy(replica => replica.ReplicaIndex == candidate.ReplicaIndex ? 0 : 1)
            .ThenBy(replica => replica.ReplicaIndex))
        {
            var path = Path.GetFullPath(GetObjectPath(replica.MountPath, candidate.ObjectId));
            if (!existingPaths.Contains(path))
            {
                return replica;
            }
        }

        return null;
    }

    private static async Task UpsertReplicaForRebalanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObjectReplicaRecord replica,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into object_replicas(
                object_id,
                replica_index,
                node_id,
                disk_id,
                pool_id,
                content_path,
                status,
                checksum_sha256,
                created_utc)
            values(
                $objectId,
                $replicaIndex,
                $nodeId,
                $diskId,
                $poolId,
                $contentPath,
                $status,
                $checksum,
                $created)
            on conflict(object_id, replica_index) do update set
                node_id = excluded.node_id,
                disk_id = excluded.disk_id,
                pool_id = excluded.pool_id,
                content_path = excluded.content_path,
                status = excluded.status,
                checksum_sha256 = excluded.checksum_sha256,
                created_utc = excluded.created_utc;
            """;
        command.Parameters.AddWithValue("$objectId", replica.ObjectId);
        command.Parameters.AddWithValue("$replicaIndex", replica.ReplicaIndex);
        command.Parameters.AddWithValue("$nodeId", replica.NodeId);
        command.Parameters.AddWithValue("$diskId", replica.DiskId);
        command.Parameters.AddWithValue("$poolId", replica.PoolId);
        command.Parameters.AddWithValue("$contentPath", replica.ContentPath);
        command.Parameters.AddWithValue("$status", replica.Status);
        command.Parameters.AddWithValue("$checksum", (object?)replica.ChecksumSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", replica.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRebalanceTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string objectId,
        int? shardIndex,
        string sourcePath,
        string? targetPath,
        string reason,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into object_rebalance_tasks(
                task_id,
                object_id,
                shard_index,
                source_path,
                target_path,
                reason,
                status,
                attempts,
                last_error,
                created_utc,
                updated_utc)
            values(
                $taskId,
                $objectId,
                $shardIndex,
                $sourcePath,
                $targetPath,
                $reason,
                $status,
                1,
                $error,
                $now,
                $now);
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$shardIndex", (object?)shardIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$targetPath", (object?)targetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordFailedRebalanceTaskAsync(
        string taskId,
        ReplicaRebalanceCandidate candidate,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await InsertRebalanceTaskAsync(
                connection,
                (SqliteTransaction)transaction,
                taskId,
                candidate.ObjectId,
                null,
                candidate.ContentPath,
                null,
                "ReplicaOfflinePlacement",
                RebalanceTaskStatusFailed,
                error.Length > 1024 ? error[..1024] : error,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Rebalance failure telemetry is best effort; the data-plane result should not depend on it.
        }
    }

    private sealed record ReplicaRebalanceCandidate(
        string ObjectId,
        string BucketName,
        string Key,
        long ContentLength,
        int ReplicaIndex,
        string ContentPath);
}
