using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<ObjectScrubResult> ScrubObjectReplicasAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var limit = Math.Clamp(maxItems, 1, 10_000);
        var candidates = new List<ObjectReplicaScrubCandidate>(limit);

        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select
                    coalesce(o.bucket_name, v.bucket_name),
                    coalesce(o.key, v.key),
                    r.object_id,
                    r.replica_index,
                    r.content_path,
                    r.checksum_sha256,
                    case when o.object_id is null then 0 else 1 end
                from object_replicas r
                left join objects o on o.object_id = r.object_id
                left join object_versions v on v.object_id = r.object_id and v.is_delete_marker = 0
                where r.checksum_sha256 is not null
                order by r.created_utc, r.object_id, r.replica_index
                limit $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    continue;
                }

                candidates.Add(new ObjectReplicaScrubCandidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetBoolean(6)));
            }
        }

        var checkedReplicas = 0;
        var missingReplicas = 0;
        var corruptReplicas = 0;
        var repairs = new List<(ObjectReplicaScrubCandidate Candidate, string Reason)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.ContentPath))
            {
                missingReplicas++;
                if (candidate.IsCurrentObject)
                {
                    repairs.Add((candidate, "ReplicaFileMissing"));
                }

                continue;
            }

            checkedReplicas++;
            var checksum = await ComputeFileSha256Async(candidate.ContentPath, cancellationToken);
            if (!string.Equals(checksum, candidate.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                corruptReplicas++;
                if (candidate.IsCurrentObject)
                {
                    repairs.Add((candidate, "ReplicaChecksumMismatch"));
                }
            }
        }

        var queuedRepairs = 0;
        if (repairs.Count > 0)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var (candidate, reason) in repairs)
            {
                await UpsertReplicaRepairAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    candidate.ObjectId,
                    candidate.BucketName,
                    candidate.Key,
                    reason,
                    cancellationToken);
                queuedRepairs++;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return new ObjectScrubResult(checkedReplicas, missingReplicas, corruptReplicas, queuedRepairs);
    }

    private sealed record ObjectReplicaScrubCandidate(
        string BucketName,
        string Key,
        string ObjectId,
        int ReplicaIndex,
        string ContentPath,
        string ChecksumSha256,
        bool IsCurrentObject);
}
