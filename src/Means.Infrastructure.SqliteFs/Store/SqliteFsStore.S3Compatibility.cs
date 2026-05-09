using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<BucketVersioningInfo> GetBucketVersioningAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        return new BucketVersioningInfo(bucketName, await GetBucketVersioningStatusAsync(connection, bucketName, cancellationToken));
    }

    public async Task<BucketVersioningInfo> PutBucketVersioningAsync(string bucketName, string status, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalizedStatus = NormalizeBucketVersioningStatus(status);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into bucket_versioning(bucket_name, status, updated_utc)
            values($bucket, $status, $updated)
            on conflict(bucket_name) do update set
                status = excluded.status,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$status", normalizedStatus);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new BucketVersioningInfo(bucketName, normalizedStatus);
    }

    public async Task<ListObjectVersionsResult> ListObjectVersionsAsync(string bucketName, ListObjectVersionsOptions options, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var prefix = options.Prefix ?? "";
        var prefixEnd = PrefixUpperBound(prefix);
        var delimiter = options.Delimiter ?? "";
        var maxKeys = options.MaxKeys < 0 ? 1000 : Math.Min(options.MaxKeys, 1000);
        if (maxKeys == 0)
        {
            return new ListObjectVersionsResult(
                bucketName,
                options.Prefix,
                options.Delimiter,
                options.KeyMarker,
                options.VersionIdMarker,
                maxKeys,
                IsTruncated: false,
                NextKeyMarker: null,
                NextVersionIdMarker: null,
                Versions: Array.Empty<ListedObjectVersion>(),
                CommonPrefixes: Array.Empty<string>());
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select key, version_id, etag, content_length, last_modified_utc, is_delete_marker
            from object_versions
            where bucket_name = $bucket
              and key >= $prefix
              and ($prefixEnd is null or key < $prefixEnd)
            order by key, created_utc desc, version_id desc;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$prefixEnd", (object?)prefixEnd ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<ListedObjectVersion>();
        var commonPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        var latestSeenKeys = new HashSet<string>(StringComparer.Ordinal);
        var markerSatisfied = string.IsNullOrWhiteSpace(options.KeyMarker);
        var emitted = 0;
        string? lastReturnedKey = null;
        string? lastReturnedVersionId = null;
        string? nextKeyMarker = null;
        string? nextVersionIdMarker = null;
        var isTruncated = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var versionId = reader.GetString(1);
            var isLatest = latestSeenKeys.Add(key);

            if (!markerSatisfied)
            {
                var markerComparison = string.CompareOrdinal(key, options.KeyMarker);
                if (markerComparison < 0)
                {
                    continue;
                }

                if (markerComparison > 0)
                {
                    markerSatisfied = true;
                }
                else if (string.IsNullOrWhiteSpace(options.VersionIdMarker))
                {
                    continue;
                }
                else if (string.Equals(versionId, options.VersionIdMarker, StringComparison.Ordinal))
                {
                    markerSatisfied = true;
                    continue;
                }
                else
                {
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(delimiter))
            {
                var rest = key[prefix.Length..];
                var delimiterIndex = rest.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimiterIndex >= 0)
                {
                    var foldedPrefix = prefix + rest[..(delimiterIndex + delimiter.Length)];
                    if (commonPrefixes.Contains(foldedPrefix))
                    {
                        continue;
                    }

                    if (emitted >= maxKeys)
                    {
                        isTruncated = true;
                        nextKeyMarker = lastReturnedKey;
                        nextVersionIdMarker = lastReturnedVersionId;
                        break;
                    }

                    commonPrefixes.Add(foldedPrefix);
                    emitted++;
                    lastReturnedKey = key;
                    lastReturnedVersionId = versionId;
                    continue;
                }
            }

            if (emitted >= maxKeys)
            {
                isTruncated = true;
                nextKeyMarker = lastReturnedKey;
                nextVersionIdMarker = lastReturnedVersionId;
                break;
            }

            versions.Add(new ListedObjectVersion(
                key,
                versionId,
                isLatest,
                reader.GetBoolean(5),
                reader.GetString(2),
                reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4))));
            emitted++;
            lastReturnedKey = key;
            lastReturnedVersionId = versionId;
        }

        return new ListObjectVersionsResult(
            bucketName,
            options.Prefix,
            options.Delimiter,
            options.KeyMarker,
            options.VersionIdMarker,
            maxKeys,
            isTruncated,
            nextKeyMarker,
            nextVersionIdMarker,
            versions,
            commonPrefixes.ToArray());
    }

    public async Task<ObjectTagSet> GetObjectTaggingAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var resolvedVersionId = await ResolveObjectVersionIdAsync(connection, bucketName, key, versionId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select name, value from object_version_tags where version_id = $versionId order by name;";
        command.Parameters.AddWithValue("$versionId", resolvedVersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags[reader.GetString(0)] = reader.GetString(1);
        }

        return new ObjectTagSet(tags);
    }

    public async Task PutObjectTaggingAsync(string bucketName, string key, string? versionId, ObjectTagSet tags, CancellationToken cancellationToken)
    {
        ValidateTags(tags);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var resolvedVersionId = await ResolveObjectVersionIdAsync(connection, bucketName, key, versionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ReplaceObjectTagsAsync(connection, (SqliteTransaction)transaction, resolvedVersionId, tags.Tags, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteObjectTaggingAsync(string bucketName, string key, string? versionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        var resolvedVersionId = await ResolveObjectVersionIdAsync(connection, bucketName, key, versionId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from object_version_tags where version_id = $versionId;";
        command.Parameters.AddWithValue("$versionId", resolvedVersionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BucketLifecycleConfiguration?> GetBucketLifecycleAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select rule_id, status, prefix, expiration_days, noncurrent_version_expiration_days, abort_incomplete_multipart_upload_days
            from bucket_lifecycle_rules
            where bucket_name = $bucket
            order by rule_id;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rules = new List<LifecycleRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new LifecycleRule(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return rules.Count == 0 ? null : new BucketLifecycleConfiguration(rules);
    }

    public async Task PutBucketLifecycleAsync(string bucketName, BucketLifecycleConfiguration configuration, CancellationToken cancellationToken)
    {
        ValidateLifecycle(configuration);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "delete from bucket_lifecycle_rules where bucket_name = $bucket;";
            delete.Parameters.AddWithValue("$bucket", bucketName);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var rule in configuration.Rules)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                insert into bucket_lifecycle_rules(
                    bucket_name,
                    rule_id,
                    status,
                    prefix,
                    expiration_days,
                    noncurrent_version_expiration_days,
                    abort_incomplete_multipart_upload_days)
                values(
                    $bucket,
                    $ruleId,
                    $status,
                    $prefix,
                    $expirationDays,
                    $noncurrentDays,
                    $abortDays);
                """;
            insert.Parameters.AddWithValue("$bucket", bucketName);
            insert.Parameters.AddWithValue("$ruleId", rule.Id);
            insert.Parameters.AddWithValue("$status", rule.Status);
            insert.Parameters.AddWithValue("$prefix", rule.Prefix);
            insert.Parameters.AddWithValue("$expirationDays", (object?)rule.ExpirationDays ?? DBNull.Value);
            insert.Parameters.AddWithValue("$noncurrentDays", (object?)rule.NoncurrentVersionExpirationDays ?? DBNull.Value);
            insert.Parameters.AddWithValue("$abortDays", (object?)rule.AbortIncompleteMultipartUploadDays ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteBucketLifecycleAsync(string bucketName, CancellationToken cancellationToken)
    {
        await DeleteBucketScopedConfigurationAsync(bucketName, "delete from bucket_lifecycle_rules where bucket_name = $bucket;", cancellationToken);
    }

    public async Task<BucketCorsConfiguration?> GetBucketCorsAsync(string bucketName, CancellationToken cancellationToken)
    {
        var xml = await GetBucketXmlConfigurationAsync(bucketName, "select cors_xml from bucket_cors where bucket_name = $bucket;", cancellationToken);
        return xml is null ? null : new BucketCorsConfiguration(xml);
    }

    public async Task PutBucketCorsAsync(string bucketName, BucketCorsConfiguration configuration, CancellationToken cancellationToken)
    {
        await PutBucketXmlConfigurationAsync(
            bucketName,
            """
            insert into bucket_cors(bucket_name, cors_xml, updated_utc)
            values($bucket, $xml, $updated)
            on conflict(bucket_name) do update set
                cors_xml = excluded.cors_xml,
                updated_utc = excluded.updated_utc;
            """,
            configuration.Xml,
            cancellationToken);
    }

    public async Task DeleteBucketCorsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await DeleteBucketScopedConfigurationAsync(bucketName, "delete from bucket_cors where bucket_name = $bucket;", cancellationToken);
    }

    public async Task<BucketNotificationConfiguration?> GetBucketNotificationAsync(string bucketName, CancellationToken cancellationToken)
    {
        var xml = await GetBucketXmlConfigurationAsync(bucketName, "select notification_xml from bucket_notifications where bucket_name = $bucket;", cancellationToken);
        return xml is null ? null : new BucketNotificationConfiguration(xml);
    }

    public async Task PutBucketNotificationAsync(string bucketName, BucketNotificationConfiguration configuration, CancellationToken cancellationToken)
    {
        await PutBucketXmlConfigurationAsync(
            bucketName,
            """
            insert into bucket_notifications(bucket_name, notification_xml, updated_utc)
            values($bucket, $xml, $updated)
            on conflict(bucket_name) do update set
                notification_xml = excluded.notification_xml,
                updated_utc = excluded.updated_utc;
            """,
            configuration.Xml,
            cancellationToken);
    }

    public async Task DeleteBucketNotificationAsync(string bucketName, CancellationToken cancellationToken)
    {
        await DeleteBucketScopedConfigurationAsync(bucketName, "delete from bucket_notifications where bucket_name = $bucket;", cancellationToken);
    }

    public async Task<int> ApplyLifecycleRulesAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var applied = await AbortLifecycleMultipartUploadsAsync(nowUtc, maxItems, cancellationToken);
        if (applied >= maxItems)
        {
            return applied;
        }

        applied += await ExpireLifecycleCurrentObjectsAsync(nowUtc, maxItems - applied, cancellationToken);
        if (applied >= maxItems)
        {
            return applied;
        }

        applied += await ExpireLifecycleNoncurrentVersionsAsync(nowUtc, maxItems - applied, cancellationToken);
        return applied;
    }

    private async Task<int> AbortLifecycleMultipartUploadsAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var candidates = new List<(string Bucket, string Key, string UploadId)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select u.bucket_name, u.key, u.upload_id
                from multipart_uploads u
                join bucket_lifecycle_rules r on r.bucket_name = u.bucket_name
                where r.status = 'Enabled'
                  and r.abort_incomplete_multipart_upload_days is not null
                  and u.key like r.prefix || '%'
                  and julianday(u.initiated_utc) < julianday($now) - r.abort_incomplete_multipart_upload_days
                order by u.initiated_utc
                limit $limit;
                """;
            command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", maxItems);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var applied = 0;
        foreach (var item in candidates)
        {
            await AbortMultipartUploadAsync(item.Bucket, item.Key, item.UploadId, cancellationToken);
            applied++;
        }

        return applied;
    }

    private async Task<int> ExpireLifecycleCurrentObjectsAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var candidates = new List<(string Bucket, string Key)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select distinct o.bucket_name, o.key
                from objects o
                join bucket_lifecycle_rules r on r.bucket_name = o.bucket_name
                where r.status = 'Enabled'
                  and r.expiration_days is not null
                  and o.key like r.prefix || '%'
                  and julianday(o.last_modified_utc) < julianday($now) - r.expiration_days
                order by o.bucket_name, o.key
                limit $limit;
                """;
            command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", maxItems);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var applied = 0;
        foreach (var item in candidates)
        {
            await DeleteObjectAsync(item.Bucket, item.Key, versionId: null, cancellationToken);
            applied++;
        }

        return applied;
    }

    private async Task<int> ExpireLifecycleNoncurrentVersionsAsync(DateTimeOffset nowUtc, int maxItems, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var candidates = new List<(string Bucket, string Key, string VersionId)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select v.bucket_name, v.key, v.version_id
                from object_versions v
                join bucket_lifecycle_rules r on r.bucket_name = v.bucket_name
                where r.status = 'Enabled'
                  and r.noncurrent_version_expiration_days is not null
                  and v.key like r.prefix || '%'
                  and julianday(v.created_utc) < julianday($now) - r.noncurrent_version_expiration_days
                  and v.version_id not in (
                      select ov.version_id
                      from object_versions ov
                      where ov.bucket_name = v.bucket_name and ov.key = v.key
                      order by ov.created_utc desc
                      limit 1
                  )
                order by v.created_utc
                limit $limit;
                """;
            command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", maxItems);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var applied = 0;
        foreach (var item in candidates)
        {
            await DeleteObjectAsync(item.Bucket, item.Key, item.VersionId, cancellationToken);
            applied++;
        }

        return applied;
    }

    private async Task<DeleteObjectResult> DeleteObjectVersionAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        string versionId,
        CancellationToken cancellationToken)
    {
        var version = await GetObjectVersionRecordAsync(connection, bucketName, key, versionId, cancellationToken)
            ?? throw new MeansException(MeansErrorCodes.NoSuchVersion, "Object version does not exist.", 404);
        var replicaPaths = version.IsDeleteMarker ? Array.Empty<string>() : await GetObjectReplicaPathsAsync(connection, version.ObjectId, cancellationToken);
        var ecShardPaths = version.IsDeleteMarker ? Array.Empty<string>() : await GetObjectEcShardPathsAsync(connection, version.ObjectId, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteVersion = connection.CreateCommand())
            {
                deleteVersion.Transaction = (SqliteTransaction)transaction;
                deleteVersion.CommandText = "delete from object_versions where version_id = $versionId;";
                deleteVersion.Parameters.AddWithValue("$versionId", versionId);
                await deleteVersion.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!version.IsDeleteMarker)
            {
                await DeleteObjectReplicaRowsAsync(connection, (SqliteTransaction)transaction, version.ObjectId, cancellationToken);
                await DeleteReplicaRepairRowsAsync(connection, (SqliteTransaction)transaction, version.ObjectId, cancellationToken);
                await DeleteObjectErasureCodingRowsAsync(connection, (SqliteTransaction)transaction, version.ObjectId, cancellationToken);
            }

            await RefreshCurrentObjectFromLatestVersionAsync(connection, (SqliteTransaction)transaction, bucketName, key, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        DeleteObjectFilesQuietly(version.ObjectId, replicaPaths);
        DeleteEcShardFilesQuietly(ecShardPaths);
        return new DeleteObjectResult(bucketName, key, versionId, version.IsDeleteMarker);
    }

    private async Task<string> ResolveObjectVersionIdAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var version = await GetObjectVersionRecordAsync(connection, bucketName, key, versionId, cancellationToken);
            if (version is null || version.IsDeleteMarker)
            {
                throw new MeansException(MeansErrorCodes.NoSuchVersion, "Object version does not exist.", 404);
            }

            return version.VersionId;
        }

        var objectId = await GetObjectIdAsync(connection, bucketName, key, cancellationToken);
        if (objectId is null)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
        }

        return objectId;
    }

    private async Task<ObjectInfo?> GetObjectVersionInfoAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        string versionId,
        CancellationToken cancellationToken)
    {
        var version = await GetObjectVersionRecordAsync(connection, bucketName, key, versionId, cancellationToken);
        if (version is null)
        {
            throw new MeansException(MeansErrorCodes.NoSuchVersion, "Object version does not exist.", 404);
        }

        if (version.IsDeleteMarker)
        {
            throw new MeansException(
                MeansErrorCodes.NoSuchKey,
                "Object version is a delete marker.",
                404,
                new Dictionary<string, string>
                {
                    ["x-amz-delete-marker"] = "true",
                    ["x-amz-version-id"] = version.VersionId
                });
        }

        var metadata = await GetObjectVersionMetadataAsync(connection, version.VersionId, cancellationToken);
        return new ObjectInfo(
            version.BucketName,
            version.Key,
            version.ObjectId,
            version.ETag,
            version.ContentLength,
            version.ContentType,
            version.LastModified,
            metadata,
            version.CacheControl,
            version.ContentDisposition);
    }

    private static async Task ReplaceObjectTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string versionId,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "delete from object_version_tags where version_id = $versionId;";
            delete.Parameters.AddWithValue("$versionId", versionId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in tags)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "insert into object_version_tags(version_id, name, value) values($versionId, $name, $value);";
            insert.Parameters.AddWithValue("$versionId", versionId);
            insert.Parameters.AddWithValue("$name", item.Key);
            insert.Parameters.AddWithValue("$value", item.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateTags(ObjectTagSet tags)
    {
        if (tags.Tags.Count > 10 || tags.Tags.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 || item.Value.Length > 256))
        {
            throw new MeansException(MeansErrorCodes.InvalidTag, "Object tagging supports up to 10 non-empty tags.", 400);
        }
    }

    private static void ValidateLifecycle(BucketLifecycleConfiguration configuration)
    {
        if (configuration.Rules.Count == 0 || configuration.Rules.Count > 1000)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Lifecycle configuration must include 1 to 1000 rules.", 400);
        }

        foreach (var rule in configuration.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)
                || !string.Equals(rule.Status, "Enabled", StringComparison.Ordinal)
                   && !string.Equals(rule.Status, "Disabled", StringComparison.Ordinal)
                || (rule.ExpirationDays is not null && rule.ExpirationDays <= 0)
                || (rule.NoncurrentVersionExpirationDays is not null && rule.NoncurrentVersionExpirationDays <= 0)
                || (rule.AbortIncompleteMultipartUploadDays is not null && rule.AbortIncompleteMultipartUploadDays <= 0))
            {
                throw new MeansException(MeansErrorCodes.MalformedXML, "Invalid lifecycle rule.", 400);
            }
        }
    }

    private async Task<string?> GetBucketXmlConfigurationAsync(string bucketName, string sql, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$bucket", bucketName);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task PutBucketXmlConfigurationAsync(string bucketName, string sql, string xml, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$xml", xml);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteBucketScopedConfigurationAsync(string bucketName, string sql, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await BucketExistsAsync(connection, bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$bucket", bucketName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeBucketVersioningStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, BucketVersioningStatuses.Off, StringComparison.OrdinalIgnoreCase))
        {
            return BucketVersioningStatuses.Off;
        }

        if (string.Equals(status, BucketVersioningStatuses.Enabled, StringComparison.OrdinalIgnoreCase))
        {
            return BucketVersioningStatuses.Enabled;
        }

        if (string.Equals(status, BucketVersioningStatuses.Suspended, StringComparison.OrdinalIgnoreCase))
        {
            return BucketVersioningStatuses.Suspended;
        }

        throw new MeansException(MeansErrorCodes.MalformedXML, "Invalid bucket versioning status.", 400);
    }

    private async Task<bool> IsBucketVersioningEnabledAsync(SqliteConnection connection, string bucketName, CancellationToken cancellationToken)
    {
        return string.Equals(await GetBucketVersioningStatusAsync(connection, bucketName, cancellationToken), BucketVersioningStatuses.Enabled, StringComparison.Ordinal);
    }

    private static async Task<string> GetBucketVersioningStatusAsync(SqliteConnection connection, string bucketName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from bucket_versioning where bucket_name = $bucket;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? BucketVersioningStatuses.Off;
    }

    private static async Task InsertDeleteMarkerVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucketName,
        string key,
        string versionId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into object_versions(
                version_id,
                bucket_name,
                key,
                object_id,
                etag,
                content_length,
                content_type,
                last_modified_utc,
                cache_control,
                content_disposition,
                is_delete_marker,
                created_utc)
            values(
                $versionId,
                $bucket,
                $key,
                $versionId,
                '',
                0,
                'application/octet-stream',
                $created,
                null,
                null,
                1,
                $created);
            """;
        command.Parameters.AddWithValue("$versionId", versionId);
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> DeleteCurrentObjectRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucketName,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            delete from object_metadata where bucket_name = $bucket and key = $key;
            delete from objects where bucket_name = $bucket and key = $key;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteObjectVersionRowsForObjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucketName,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from object_versions where bucket_name = $bucket and key = $key;";
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RefreshCurrentObjectFromLatestVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bucketName,
        string key,
        CancellationToken cancellationToken)
    {
        await DeleteCurrentObjectRowsAsync(connection, transaction, bucketName, key, cancellationToken);
        var latest = await GetLatestObjectVersionRecordAsync(connection, bucketName, key, cancellationToken);
        if (latest is null || latest.IsDeleteMarker)
        {
            return;
        }

        var metadata = await GetObjectVersionMetadataAsync(connection, latest.VersionId, cancellationToken);
        await UpsertObjectAsync(
            connection,
            transaction,
            new ObjectInfo(
                latest.BucketName,
                latest.Key,
                latest.ObjectId,
                latest.ETag,
                latest.ContentLength,
                latest.ContentType,
                latest.LastModified,
                metadata,
                latest.CacheControl,
                latest.ContentDisposition),
            cancellationToken);
    }

    private static async Task<ObjectVersionRecord?> GetLatestObjectVersionRecordAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select version_id, bucket_name, key, object_id, etag, content_length, content_type, last_modified_utc, cache_control, content_disposition, is_delete_marker, created_utc
            from object_versions
            where bucket_name = $bucket and key = $key
            order by created_utc desc, version_id desc
            limit 1;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadObjectVersionRecord(reader) : null;
    }

    private static async Task<ObjectVersionRecord?> GetObjectVersionRecordAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select version_id, bucket_name, key, object_id, etag, content_length, content_type, last_modified_utc, cache_control, content_disposition, is_delete_marker, created_utc
            from object_versions
            where bucket_name = $bucket and key = $key and version_id = $versionId;
            """;
        command.Parameters.AddWithValue("$bucket", bucketName);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$versionId", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadObjectVersionRecord(reader) : null;
    }

    private static ObjectVersionRecord ReadObjectVersionRecord(SqliteDataReader reader)
    {
        return new ObjectVersionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetBoolean(10),
            DateTimeOffset.Parse(reader.GetString(11)));
    }

    private sealed record ObjectVersionRecord(
        string VersionId,
        string BucketName,
        string Key,
        string ObjectId,
        string ETag,
        long ContentLength,
        string ContentType,
        DateTimeOffset LastModified,
        string? CacheControl,
        string? ContentDisposition,
        bool IsDeleteMarker,
        DateTimeOffset CreatedAt);
}

