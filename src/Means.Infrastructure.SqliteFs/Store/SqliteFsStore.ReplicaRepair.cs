using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string ReplicaRepairStatusPending = "Pending";
    private const string ReplicaRepairStatusCompleted = "Completed";
    private const string ReplicaRepairStatusFailed = "Failed";

    public async Task<int> EnqueueMissingReplicaRepairsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var candidates = await ListReplicaRepairCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var queued = 0;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            if (!ReplicaRepairNeeded(candidate))
            {
                continue;
            }

            await UpsertReplicaRepairAsync(
                connection,
                (SqliteTransaction)transaction,
                candidate.ObjectId,
                candidate.BucketName,
                candidate.Key,
                RepairReason(candidate),
                cancellationToken);
            queued++;
        }

        await transaction.CommitAsync(cancellationToken);
        return queued;
    }

    public async Task<int> RepairQueuedReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var batch = await ReadReplicaRepairBatchAsync(Math.Clamp(maxItems, 1, 1000), cancellationToken);
        var repaired = 0;
        foreach (var item in batch)
        {
            if (await RepairOneReplicaSetAsync(item, cancellationToken))
            {
                repaired++;
            }
        }

        return repaired;
    }

    private async Task<IReadOnlyList<ReplicaRepairCandidate>> ListReplicaRepairCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var objects = new List<(string BucketName, string Key, string ObjectId, long ContentLength)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            select bucket_name, key, object_id, content_length
            from objects
            order by bucket_name, key;
            """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                objects.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }

        var candidates = new List<ReplicaRepairCandidate>(objects.Count);
        foreach (var item in objects)
        {
            var replicas = await GetObjectReplicasAsync(connection, item.ObjectId, cancellationToken);
            candidates.Add(new ReplicaRepairCandidate(
                item.BucketName,
                item.Key,
                item.ObjectId,
                item.ContentLength,
                replicas));
        }

        return candidates;
    }

    private bool ReplicaRepairNeeded(ReplicaRepairCandidate candidate)
    {
        var desiredReplicas = Math.Clamp(_options.ReplicaCount, 1, 16);
        if (candidate.Replicas.Count < desiredReplicas)
        {
            return true;
        }

        if (candidate.Replicas.Any(replica => !File.Exists(replica.ContentPath)))
        {
            return true;
        }

        if (candidate.Replicas.Count == 0 && File.Exists(GetObjectPath(candidate.ObjectId)))
        {
            return desiredReplicas > 1;
        }

        return false;
    }

    private static string RepairReason(ReplicaRepairCandidate candidate)
    {
        if (candidate.Replicas.Count == 0)
        {
            return "NoReplicaManifest";
        }

        return candidate.Replicas.Any(replica => !File.Exists(replica.ContentPath))
            ? "ReplicaFileMissing"
            : "ReplicaCountBelowTarget";
    }

    private static async Task UpsertReplicaRepairAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        string bucketName,
        string key,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into object_replica_repairs(
                object_id,
                bucket_name,
                key,
                reason,
                status,
                attempts,
                last_error,
                created_utc,
                updated_utc)
            values(
                $objectId,
                $bucket,
                $key,
                $reason,
                $status,
                0,
                null,
                $now,
                $now)
            on conflict(object_id) do update set
                bucket_name = excluded.bucket_name,
                key = excluded.key,
                reason = excluded.reason,
                status = $status,
                attempts = 0,
                last_error = null,
                updated_utc = $now;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$status", ReplicaRepairStatusPending);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteReplicaRepairRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from object_replica_repairs where object_id = $objectId;";
        command.Parameters.AddWithValue("$objectId", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ReplicaRepairItem>> ReadReplicaRepairBatchAsync(int maxItems, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, bucket_name, key, attempts
            from object_replica_repairs
            where status in ($pending, $failed)
              and attempts < $maxAttempts
            order by updated_utc, object_id
            limit $limit;
            """;
        command.Parameters.AddWithValue("$pending", ReplicaRepairStatusPending);
        command.Parameters.AddWithValue("$failed", ReplicaRepairStatusFailed);
        command.Parameters.AddWithValue("$maxAttempts", Math.Max(1, _options.ReplicaRepairMaxAttempts));
        command.Parameters.AddWithValue("$limit", maxItems);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ReplicaRepairItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReplicaRepairItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return items;
    }

    private async Task<bool> RepairOneReplicaSetAsync(ReplicaRepairItem item, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var objectInfo = await GetObjectInfoAsync(connection, item.BucketName, item.Key, cancellationToken);
            if (objectInfo is null || objectInfo.ObjectId != item.ObjectId)
            {
                await CompleteReplicaRepairAsync(connection, item.ObjectId, cancellationToken);
                return false;
            }

            var replicas = await GetObjectReplicasAsync(connection, item.ObjectId, cancellationToken);
            var sourcePath = await FindReadableReplicaPathAsync(item.ObjectId, replicas, cancellationToken);
            if (sourcePath is null)
            {
                throw new InvalidOperationException("No readable replica is available for repair.");
            }

            var desiredReplicas = Math.Clamp(_options.ReplicaCount, 1, 16);
            var repaired = await RepairManifestReplicasAsync(sourcePath, replicas, cancellationToken);
            if (replicas.Count < desiredReplicas)
            {
                repaired += await AddMissingReplicaRecordsAsync(connection, objectInfo, sourcePath, replicas, cancellationToken);
            }

            await CompleteReplicaRepairAsync(connection, item.ObjectId, cancellationToken);
            return repaired > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailReplicaRepairAsync(item.ObjectId, item.Attempts + 1, ex.Message, cancellationToken);
            return false;
        }
    }

    private async Task<string?> FindReadableReplicaPathAsync(
        string objectId,
        IReadOnlyList<ObjectReplicaRecord> replicas,
        CancellationToken cancellationToken)
    {
        foreach (var replica in replicas)
        {
            if (File.Exists(replica.ContentPath) && await IsReadableReplicaAsync(replica, cancellationToken))
            {
                return replica.ContentPath;
            }
        }

        var fallback = GetObjectPath(objectId);
        return File.Exists(fallback) ? fallback : null;
    }

    private async Task<int> RepairManifestReplicasAsync(
        string sourcePath,
        IReadOnlyList<ObjectReplicaRecord> replicas,
        CancellationToken cancellationToken)
    {
        var repaired = 0;
        foreach (var replica in replicas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSamePath(sourcePath, replica.ContentPath))
            {
                continue;
            }

            var needsRepair = !File.Exists(replica.ContentPath);
            if (!needsRepair && !await IsReadableReplicaAsync(replica, cancellationToken))
            {
                needsRepair = true;
            }

            if (!needsRepair)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(replica.ContentPath)!);
            var tempPath = replica.ContentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(sourcePath, tempPath, overwrite: false);
                File.Move(tempPath, replica.ContentPath, overwrite: true);
            }
            catch
            {
                DeleteFileQuietly(tempPath);
                throw;
            }

            repaired++;
        }

        return repaired;
    }

    private async Task<int> AddMissingReplicaRecordsAsync(
        SqliteConnection connection,
        ObjectInfo objectInfo,
        string sourcePath,
        IReadOnlyList<ObjectReplicaRecord> existingReplicas,
        CancellationToken cancellationToken)
    {
        var desiredReplicas = Math.Clamp(_options.ReplicaCount, 1, 16);
        var plan = await PlanObjectReplicasAsync(
            objectInfo.BucketName,
            objectInfo.Key,
            objectInfo.ObjectId,
            objectInfo.ContentLength,
            cancellationToken);
        var existingIndexes = existingReplicas.Select(replica => replica.ReplicaIndex).ToHashSet();
        var existingPaths = existingReplicas.Select(replica => Path.GetFullPath(replica.ContentPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<ObjectReplicaRecord>();
        foreach (var replica in plan.Replicas.Where(replica => !existingIndexes.Contains(replica.ReplicaIndex)))
        {
            if (created.Count + existingReplicas.Count >= desiredReplicas)
            {
                break;
            }

            var contentPath = GetObjectPath(replica.MountPath, objectInfo.ObjectId);
            if (!existingPaths.Add(Path.GetFullPath(contentPath)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
            File.Copy(sourcePath, contentPath, overwrite: false);
            var checksum = await ComputeFileSha256Async(contentPath, cancellationToken);
            created.Add(new ObjectReplicaRecord(
                objectInfo.ObjectId,
                replica.ReplicaIndex,
                replica.NodeId,
                replica.DiskId,
                replica.PoolId,
                contentPath,
                ObjectReplicaStatusCommitted,
                checksum,
                DateTimeOffset.UtcNow));
        }

        if (created.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var replica in created)
            {
                await InsertObjectReplicaAsync(connection, (SqliteTransaction)transaction, replica, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteReplicaFilesQuietly(created);
            throw;
        }

        return created.Count;
    }

    private static async Task InsertObjectReplicaAsync(
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
                $created);
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

    private static async Task CompleteReplicaRepairAsync(SqliteConnection connection, string objectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update object_replica_repairs
            set status = $status,
                last_error = null,
                updated_utc = $updated
            where object_id = $objectId;
            """;
        command.Parameters.AddWithValue("$status", ReplicaRepairStatusCompleted);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$objectId", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailReplicaRepairAsync(string objectId, int attempts, string error, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update object_replica_repairs
            set status = $status,
                attempts = $attempts,
                last_error = $error,
                updated_utc = $updated
            where object_id = $objectId;
            """;
        command.Parameters.AddWithValue("$status", ReplicaRepairStatusFailed);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$error", error.Length > 1024 ? error[..1024] : error);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$objectId", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ReplicaRepairCandidate(
        string BucketName,
        string Key,
        string ObjectId,
        long ContentLength,
        IReadOnlyList<ObjectReplicaRecord> Replicas);

    private sealed record ReplicaRepairItem(string ObjectId, string BucketName, string Key, int Attempts);
}
