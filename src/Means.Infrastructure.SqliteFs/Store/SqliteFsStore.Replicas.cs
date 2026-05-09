using Means.Core;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string ObjectReplicaStatusCommitted = "Committed";

    private async Task<IReadOnlyList<ObjectReplicaRecord>> WriteObjectReplicasAsync(
        string bucketName,
        string key,
        string objectId,
        long contentLength,
        string sourcePath,
        string checksumSha256,
        CancellationToken cancellationToken)
    {
        var plan = await PlanObjectReplicasAsync(bucketName, key, objectId, contentLength, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var replicas = new List<ObjectReplicaRecord>(plan.Replicas.Count);
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var replica in plan.Replicas)
            {
                var contentPath = GetObjectPath(replica.MountPath, objectId);
                var fullPath = Path.GetFullPath(contentPath);
                if (!writtenPaths.Add(fullPath))
                {
                    throw new MeansException(MeansErrorCodes.InvalidArgument, "Placement selected duplicate replica paths.", 503);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
                File.Copy(sourcePath, contentPath, overwrite: false);
                replicas.Add(new ObjectReplicaRecord(
                    objectId,
                    replica.ReplicaIndex,
                    replica.NodeId,
                    replica.DiskId,
                    replica.PoolId,
                    contentPath,
                    ObjectReplicaStatusCommitted,
                    checksumSha256,
                    now));
            }
        }
        catch
        {
            foreach (var replica in replicas)
            {
                DeleteFileQuietly(replica.ContentPath);
            }

            throw;
        }

        return replicas;
    }

    private async Task<ObjectPlacementPlan> PlanObjectReplicasAsync(
        string bucketName,
        string key,
        string objectId,
        long contentLength,
        CancellationToken cancellationToken)
    {
        var replicaCount = Math.Clamp(_options.ReplicaCount, 1, 16);
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(5, _options.ReplicaOfflineAfterSeconds)));
        var topology = await GetClusterTopologyAsync(cutoff, cancellationToken);
        if (topology.Nodes.Count == 0)
        {
            if (replicaCount == 1)
            {
                return SingleReplicaFallback(bucketName, key, objectId);
            }

            throw new MeansException(MeansErrorCodes.InvalidArgument, "No online storage nodes are available for replica placement.", 503);
        }

        return _placementPlanner.PlanPlacement(
            new ObjectPlacementRequest(bucketName, key, objectId, replicaCount, contentLength),
            topology);
    }

    private ObjectPlacementPlan SingleReplicaFallback(string bucketName, string key, string objectId)
    {
        return new ObjectPlacementPlan(
            bucketName,
            key,
            objectId,
            [
                new ObjectPlacementReplica(
                    0,
                    "local-node",
                    "local-objects",
                    "pool-1",
                    _options.ObjectsPath)
            ]);
    }

    private static async Task ReplaceObjectReplicasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        IReadOnlyList<ObjectReplicaRecord> replicas,
        CancellationToken cancellationToken)
    {
        await DeleteObjectReplicaRowsAsync(connection, transaction, objectId, cancellationToken);
        foreach (var replica in replicas)
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
    }

    private static async Task DeleteObjectReplicaRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from object_replicas where object_id = $objectId;";
        command.Parameters.AddWithValue("$objectId", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> GetObjectReplicaPathsAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        var replicas = await GetObjectReplicasAsync(connection, objectId, cancellationToken);
        return replicas.Select(replica => replica.ContentPath).ToArray();
    }

    private static async Task<IReadOnlyList<ObjectReplicaRecord>> GetObjectReplicasAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                object_id,
                replica_index,
                node_id,
                disk_id,
                pool_id,
                content_path,
                status,
                checksum_sha256,
                created_utc
            from object_replicas
            where object_id = $objectId
            order by replica_index;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var replicas = new List<ObjectReplicaRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            replicas.Add(new ObjectReplicaRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8))));
        }

        return replicas;
    }

    private async Task<string> GetReadableObjectPathAsync(string objectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var paths = await GetObjectReplicaPathsAsync(connection, objectId, cancellationToken);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var fallbackPath = GetObjectPath(objectId);
        if (File.Exists(fallbackPath))
        {
            return fallbackPath;
        }

        throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is missing.", 404);
    }

    private async Task<Stream> OpenObjectContentStreamAsync(string objectId, CancellationToken cancellationToken)
    {
        return (await OpenObjectContentAsync(objectId, cancellationToken)).Content;
    }

    private async Task<(Stream Content, string? ContentPath)> OpenObjectContentAsync(string objectId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var replicas = await GetObjectReplicasAsync(connection, objectId, cancellationToken);
        var missingReplicaDetected = false;
        var corruptReplicaDetected = false;
        foreach (var replica in replicas)
        {
            if (!File.Exists(replica.ContentPath))
            {
                missingReplicaDetected = true;
                continue;
            }

            if (await IsReadableReplicaAsync(replica, cancellationToken))
            {
                if (missingReplicaDetected || corruptReplicaDetected)
                {
                    await QueueReplicaRepairForReadFallbackAsync(
                        connection,
                        objectId,
                        corruptReplicaDetected ? "ReplicaChecksumMismatch" : "ReplicaFileMissing",
                        cancellationToken);
                }

                return (new FileStream(replica.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), replica.ContentPath);
            }

            corruptReplicaDetected = true;
        }

        var fallbackPath = GetObjectPath(objectId);
        if (File.Exists(fallbackPath))
        {
            if (missingReplicaDetected || corruptReplicaDetected)
            {
                await QueueReplicaRepairForReadFallbackAsync(
                    connection,
                    objectId,
                    corruptReplicaDetected ? "ReplicaChecksumMismatch" : "ReplicaFileMissing",
                    cancellationToken);
            }

            return (new FileStream(fallbackPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), fallbackPath);
        }

        var ecStream = await TryOpenErasureCodedObjectAsync(objectId, cancellationToken);
        if (ecStream is not null)
        {
            if (missingReplicaDetected || corruptReplicaDetected)
            {
                await QueueReplicaRepairForReadFallbackAsync(
                    connection,
                    objectId,
                    corruptReplicaDetected ? "ReplicaChecksumMismatch" : "ReplicaFileMissing",
                    cancellationToken);
            }

            return (ecStream, ecStream.Name);
        }

        if (missingReplicaDetected || corruptReplicaDetected)
        {
            await QueueReplicaRepairForReadFallbackAsync(
                connection,
                objectId,
                corruptReplicaDetected ? "ReplicaChecksumMismatch" : "ReplicaFileMissing",
                cancellationToken);
        }

        throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is missing.", 404);
    }

    private async Task<bool> IsReadableReplicaAsync(ObjectReplicaRecord replica, CancellationToken cancellationToken)
    {
        if (!_options.VerifyChecksumOnRead || string.IsNullOrWhiteSpace(replica.ChecksumSha256))
        {
            return true;
        }

        try
        {
            var checksum = await ComputeFileSha256Async(replica.ContentPath, cancellationToken);
            return string.Equals(checksum, replica.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<(string BucketName, string Key)?> ReadObjectIdentityForRepairAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select bucket_name, key
            from objects
            where object_id = $objectId
            union all
            select bucket_name, key
            from object_versions
            where object_id = $objectId and is_delete_marker = 0
            limit 1;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private async Task QueueReplicaRepairForReadFallbackAsync(
        SqliteConnection connection,
        string objectId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await ReadObjectIdentityForRepairAsync(connection, objectId, cancellationToken);
            if (identity is null)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await UpsertReplicaRepairAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    objectId,
                    identity.Value.BucketName,
                    identity.Value.Key,
                    reason,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A foreground read should not fail solely because repair enqueue failed.
        }
    }

    private static void DeleteReplicaFilesQuietly(IReadOnlyList<ObjectReplicaRecord> replicas)
    {
        foreach (var replica in replicas)
        {
            DeleteFileQuietly(replica.ContentPath);
        }
    }

    private void DeleteObjectFilesQuietly(string objectId, IReadOnlyList<string> replicaPaths)
    {
        if (replicaPaths.Count == 0)
        {
            DeleteFileQuietly(GetObjectPath(objectId));
            return;
        }

        foreach (var path in replicaPaths)
        {
            DeleteFileQuietly(path);
        }
    }

    private string GetObjectPath(string mountPath, string objectId)
    {
        return Path.Combine(mountPath, objectId[..2], objectId);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await sha256.ComputeHashAsync(input, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ObjectReplicaRecord(
        string ObjectId,
        int ReplicaIndex,
        string NodeId,
        string DiskId,
        string PoolId,
        string ContentPath,
        string Status,
        string? ChecksumSha256,
        DateTimeOffset CreatedAt);
}
