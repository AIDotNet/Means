using System.Text.Json;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task<BucketVersioningInfo> GetBucketVersioningAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        return new BucketVersioningInfo(bucketName, await GetBucketVersioningStatusAsync(bucketName, cancellationToken));
    }

    public async Task<BucketVersioningInfo> PutBucketVersioningAsync(
        string bucketName,
        string status,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var normalized = NormalizeVersioningStatus(status);
        await Db.PutJsonAsync(Keys.BucketVersioning(bucketName), normalized, cancellationToken);
        return new BucketVersioningInfo(bucketName, normalized);
    }

    public async Task<ObjectTagSet> GetObjectTaggingAsync(
        string bucketName,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var record = await ReadObjectRecordAsync(bucketName, key, versionId, cancellationToken);
        if (record.IsDeleteMarker)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
        }

        return new ObjectTagSet(record.Tags);
    }

    public async Task PutObjectTaggingAsync(
        string bucketName,
        string key,
        string? versionId,
        ObjectTagSet tags,
        CancellationToken cancellationToken)
    {
        var record = await ReadObjectRecordAsync(bucketName, key, versionId, cancellationToken);
        if (record.IsDeleteMarker)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
        }

        var normalized = NormalizeMetadata(tags.Tags);
        var updated = record with { Tags = normalized };
        var mutations = new List<LogDbMutation>
        {
            new(Keys.Version(bucketName, key, record.VersionId), Serialize(updated), false)
        };
        var current = await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken);
        if (current?.VersionId == record.VersionId)
        {
            mutations.Add(new LogDbMutation(Keys.CurrentObject(bucketName, key), Serialize(updated), false));
        }

        await Db.PutBatchAsync(mutations, cancellationToken);
        await UpdateManifestTagsAsync(updated, normalized, cancellationToken);
    }

    public Task DeleteObjectTaggingAsync(
        string bucketName,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        return PutObjectTaggingAsync(
            bucketName,
            key,
            versionId,
            new ObjectTagSet(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken);
    }

    public async Task<BucketLifecycleConfiguration?> GetBucketLifecycleAsync(
        string bucketName,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        return await Db.GetJsonAsync<BucketLifecycleConfiguration>(Keys.Lifecycle(bucketName), cancellationToken);
    }

    public async Task PutBucketLifecycleAsync(
        string bucketName,
        BucketLifecycleConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutJsonAsync(Keys.Lifecycle(bucketName), configuration, cancellationToken);
    }

    public async Task DeleteBucketLifecycleAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutBatchAsync([new LogDbMutation(Keys.Lifecycle(bucketName), null, true)], cancellationToken);
    }

    public async Task<int> ApplyLifecycleRulesAsync(
        DateTimeOffset nowUtc,
        int maxItems,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var limit = Math.Clamp(maxItems, 1, 100_000);
        var applied = 0;
        foreach (var bucket in await ListBucketsAsync(cancellationToken))
        {
            var config = await GetBucketLifecycleAsync(bucket.Name, cancellationToken);
            if (config is null)
            {
                continue;
            }

            foreach (var rule in config.Rules.Where(rule => string.Equals(rule.Status, "Enabled", StringComparison.OrdinalIgnoreCase)))
            {
                if (rule.ExpirationDays is int expirationDays)
                {
                    var cutoff = nowUtc.ToUniversalTime().Subtract(TimeSpan.FromDays(expirationDays));
                    var rows = await Db.ScanPrefixAsync(Keys.CurrentObjectPrefix(bucket.Name) + Escape(rule.Prefix ?? string.Empty), limit, null, cancellationToken);
                    foreach (var row in rows)
                    {
                        if (applied >= limit)
                        {
                            return applied;
                        }

                        var record = Deserialize<XlObjectRecord>(row.Value);
                        if (!record.IsDeleteMarker && record.LastModified <= cutoff)
                        {
                            await DeleteObjectAsync(record.BucketName, record.Key, cancellationToken);
                            applied++;
                        }
                    }
                }

                if (rule.NoncurrentVersionExpirationDays is int noncurrentDays)
                {
                    var current = (await Db.ScanPrefixAsync(Keys.CurrentObjectPrefix(bucket.Name), 100_000, null, cancellationToken))
                        .Select(row => Deserialize<XlObjectRecord>(row.Value))
                        .ToDictionary(row => row.Key, row => row.VersionId, StringComparer.Ordinal);
                    var cutoff = nowUtc.ToUniversalTime().Subtract(TimeSpan.FromDays(noncurrentDays));
                    var versionRows = await Db.ScanPrefixAsync(Keys.VersionPrefix(bucket.Name), 100_000, null, cancellationToken);
                    foreach (var row in versionRows)
                    {
                        if (applied >= limit)
                        {
                            return applied;
                        }

                        var record = Deserialize<XlObjectRecord>(row.Value);
                        if ((rule.Prefix is null || record.Key.StartsWith(rule.Prefix, StringComparison.Ordinal))
                            && record.LastModified <= cutoff
                            && (!current.TryGetValue(record.Key, out var currentVersion) || currentVersion != record.VersionId))
                        {
                            await Db.PutBatchAsync([new LogDbMutation(Keys.Version(record.BucketName, record.Key, record.VersionId), null, true)], cancellationToken);
                            DeleteObjectFilesQuietly(record);
                            applied++;
                        }
                    }
                }

                if (rule.AbortIncompleteMultipartUploadDays is int abortDays)
                {
                    var cutoff = nowUtc.ToUniversalTime().Subtract(TimeSpan.FromDays(abortDays));
                    var uploads = await Db.ScanPrefixAsync(Keys.MultipartUploadPrefix(bucket.Name) + Escape(rule.Prefix ?? string.Empty), 100_000, null, cancellationToken);
                    foreach (var row in uploads)
                    {
                        if (applied >= limit)
                        {
                            return applied;
                        }

                        var upload = Deserialize<XlMultipartUploadRecord>(row.Value);
                        if (upload.InitiatedAt <= cutoff)
                        {
                            await AbortMultipartUploadAsync(upload.BucketName, upload.Key, upload.UploadId, cancellationToken);
                            applied++;
                        }
                    }
                }
            }
        }

        return applied;
    }

    public async Task<BucketCorsConfiguration?> GetBucketCorsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        return await Db.GetJsonAsync<BucketCorsConfiguration>(Keys.Cors(bucketName), cancellationToken);
    }

    public async Task PutBucketCorsAsync(
        string bucketName,
        BucketCorsConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutJsonAsync(Keys.Cors(bucketName), configuration, cancellationToken);
    }

    public async Task DeleteBucketCorsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutBatchAsync([new LogDbMutation(Keys.Cors(bucketName), null, true)], cancellationToken);
    }

    public async Task<BucketNotificationConfiguration?> GetBucketNotificationAsync(
        string bucketName,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        return await Db.GetJsonAsync<BucketNotificationConfiguration>(Keys.Notification(bucketName), cancellationToken);
    }

    public async Task PutBucketNotificationAsync(
        string bucketName,
        BucketNotificationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutJsonAsync(Keys.Notification(bucketName), configuration, cancellationToken);
    }

    public async Task DeleteBucketNotificationAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        await Db.PutBatchAsync([new LogDbMutation(Keys.Notification(bucketName), null, true)], cancellationToken);
    }

    private async Task UpdateManifestTagsAsync(
        XlObjectRecord record,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        foreach (var disk in _disks)
        {
            var path = Path.Combine(disk.RootPath, ObjectManifestRelativePath(record.BucketName, record.ObjectId));
            if (!File.Exists(path))
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize<XlObjectManifest>(await File.ReadAllTextAsync(path, cancellationToken));
            if (manifest is null)
            {
                continue;
            }

            var updated = manifest with { Tags = tags };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(updated), cancellationToken);
        }
    }
}
