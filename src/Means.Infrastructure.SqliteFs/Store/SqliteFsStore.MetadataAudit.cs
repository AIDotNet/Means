using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<MetadataConsistencyCheckResult> CheckMetadataConsistencyAsync(
        bool repair,
        int maxItems,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var limit = Math.Clamp(maxItems, 1, 10_000);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var objects = await ReadConsistencyObjectsAsync(connection, limit, cancellationToken);
        var desiredReplicas = Math.Clamp(_options.ReplicaCount, 1, 16);
        long missingVersions = 0;
        long repairedVersions = 0;
        long missingReplicaManifests = 0;
        long underReplicated = 0;
        long missingReplicaFiles = 0;
        long queuedRepairs = 0;
        var repairs = new List<(ConsistencyObject Object, string Reason)>();
        var versionRepairs = new List<ConsistencyObject>();

        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasObjectVersionAsync(connection, item.ObjectId, cancellationToken))
            {
                missingVersions++;
                versionRepairs.Add(item);
            }

            var replicas = await GetObjectReplicasAsync(connection, item.ObjectId, cancellationToken);
            if (replicas.Count == 0)
            {
                missingReplicaManifests++;
                repairs.Add((item, "NoReplicaManifest"));
                continue;
            }

            if (replicas.Count < desiredReplicas)
            {
                underReplicated++;
                repairs.Add((item, "ReplicaCountBelowTarget"));
            }

            var missingFilesForObject = replicas.LongCount(replica => !File.Exists(replica.ContentPath));
            if (missingFilesForObject > 0)
            {
                missingReplicaFiles += missingFilesForObject;
                repairs.Add((item, "ReplicaFileMissing"));
            }
        }

        if (repair && (versionRepairs.Count > 0 || repairs.Count > 0))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                foreach (var item in versionRepairs)
                {
                    if (await HasObjectVersionAsync(connection, (SqliteTransaction)transaction, item.ObjectId, cancellationToken))
                    {
                        continue;
                    }

                    var metadata = await ReadCurrentMetadataAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        item.BucketName,
                        item.Key,
                        cancellationToken);
                    var info = new ObjectInfo(
                        item.BucketName,
                        item.Key,
                        item.ObjectId,
                        item.ETag,
                        item.ContentLength,
                        item.ContentType,
                        item.LastModified,
                        metadata,
                        item.CacheControl,
                        item.ContentDisposition);
                    await InsertObjectVersionAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        info,
                        isDeleteMarker: false,
                        item.LastModified,
                        cancellationToken);
                    repairedVersions++;
                }

                foreach (var item in repairs
                    .GroupBy(entry => entry.Object.ObjectId, StringComparer.Ordinal)
                    .Select(group => group.First()))
                {
                    await UpsertReplicaRepairAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        item.Object.ObjectId,
                        item.Object.BucketName,
                        item.Object.Key,
                        item.Reason,
                        cancellationToken);
                    queuedRepairs++;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var orphanedReplicaRecords = await CountOrphanedReplicaRecordsAsync(connection, cancellationToken);
        var pendingCommits = await CountPendingMetadataCommitsAsync(connection, cancellationToken);
        return new MetadataConsistencyCheckResult(
            objects.Count,
            missingVersions,
            repairedVersions,
            missingReplicaManifests,
            underReplicated,
            missingReplicaFiles,
            queuedRepairs,
            orphanedReplicaRecords,
            pendingCommits);
    }

    private static async Task<IReadOnlyList<ConsistencyObject>> ReadConsistencyObjectsAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select bucket_name,
                   key,
                   object_id,
                   etag,
                   content_length,
                   content_type,
                   last_modified_utc,
                   cache_control,
                   content_disposition
            from objects
            order by bucket_name, key
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var objects = new List<ConsistencyObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new ConsistencyObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return objects;
    }

    private static async Task<bool> HasObjectVersionAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select 1 from object_versions where object_id = $objectId and is_delete_marker = 0 limit 1;";
        command.Parameters.AddWithValue("$objectId", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasObjectVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select 1 from object_versions where object_id = $objectId and is_delete_marker = 0 limit 1;";
        command.Parameters.AddWithValue("$objectId", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadCurrentMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucketName,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select name, value from object_metadata where bucket_name = $bucket and key = $key order by name;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }

    private static async Task<long> CountOrphanedReplicaRecordsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from object_replicas r
            left join object_versions v on v.object_id = r.object_id and v.is_delete_marker = 0
            where v.object_id is null;
            """;
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<long> CountPendingMetadataCommitsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from metadata_commits where status = $status;";
        command.Parameters.AddWithValue("$status", MetadataCommitStatusPending);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private sealed record ConsistencyObject(
        string BucketName,
        string Key,
        string ObjectId,
        string ETag,
        long ContentLength,
        string ContentType,
        DateTimeOffset LastModified,
        string? CacheControl,
        string? ContentDisposition);
}
