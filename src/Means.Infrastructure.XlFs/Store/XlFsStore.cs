using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore : IObjectStore,
    IAccessKeyStore,
    IBucketPolicyRepository,
    IConsoleStore,
    IClusterStore,
    IClusterShardStore,
    IErasureCodingProfileStore,
    IMetadataMaintenanceStore,
    IStorageMaintenanceOperations,
    IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const long MinimumMultipartPartSize = 5L * 1024 * 1024;
    private readonly XlFsOptions _options;
    private readonly IObjectPlacementPlanner _placementPlanner;
    private readonly IClusterShardTransport _shardTransport;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private MeansLogDb? _db;
    private IReadOnlyList<XlDisk> _disks = [];
    private long _auditId;

    public XlFsStore(IOptions<XlFsOptions> options)
        : this(options, new DeterministicObjectPlacementPlanner(), NoopClusterShardTransport.Instance)
    {
    }

    public XlFsStore(
        IOptions<XlFsOptions> options,
        IObjectPlacementPlanner placementPlanner,
        IClusterShardTransport shardTransport)
    {
        _options = options.Value;
        _options.ObjectsPath = ResolvePath(_options.ObjectsPath);
        _placementPlanner = placementPlanner;
        _shardTransport = shardTransport;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        _initLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var roots = _options.Disks.Length == 0 ? [_options.ObjectsPath] : _options.Disks.Select(ResolvePath).ToArray();
            var deploymentId = string.IsNullOrWhiteSpace(_options.DeploymentId) ? "local-xlfs" : _options.DeploymentId.Trim();
            var disks = new List<XlDisk>();
            for (var index = 0; index < roots.Length; index++)
            {
                var root = roots[index];
                Directory.CreateDirectory(root);
                var format = await EnsureDiskFormatAsync(root, deploymentId, index, cancellationToken);
                var drive = DriveInfoFor(root);
                disks.Add(new XlDisk(format.DiskId, root, format.SetIndex, true, drive.TotalSize, drive.AvailableFreeSpace));
            }

            _disks = disks;
            _db = await MeansLogDb.OpenAsync(
                Path.Combine(_disks[0].RootPath, ".means.sys", "meta"),
                cancellationToken,
                _options.MetaSyncMode);
            if (await Db.GetJsonAsync<XlAccessKeyRecord>(Keys.AccessKey(_options.DefaultAccessKey), cancellationToken) is null)
            {
                await Db.PutJsonAsync(
                    Keys.AccessKey(_options.DefaultAccessKey),
                    new XlAccessKeyRecord(_options.DefaultAccessKey, _options.DefaultSecretKey, true, DateTimeOffset.UtcNow),
                    cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<XlDiskFormat> EnsureDiskFormatAsync(
        string root,
        string deploymentId,
        int setIndex,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".means.sys", "format.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), XlJsonContext.Default.XlDiskFormat)
                ?? throw new InvalidOperationException("Invalid XlFs disk format.");
            if (existing.FormatVersion != FormatVersion ||
                !string.Equals(existing.DeploymentId, deploymentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("XlFs disk format is incompatible with this deployment.");
            }

            return existing;
        }

        var created = new XlDiskFormat(
            FormatVersion,
            deploymentId,
            "disk-" + setIndex.ToString("D2"),
            _options.SetId,
            setIndex,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(created, XlJsonContext.Default.XlDiskFormat), cancellationToken);
        return created;
    }

    private async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await Db.GetAsync(Keys.Bucket(bucketName), cancellationToken) is not null;
    }

    private async Task EnsureBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucketName, cancellationToken))
        {
            throw new MeansException(MeansErrorCodes.NoSuchBucket, "Bucket does not exist.", 404);
        }
    }

    private async Task<XlObjectRecord> ReadObjectRecordAsync(
        string bucketName,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(bucketName, cancellationToken);
        var record = string.IsNullOrWhiteSpace(versionId)
            ? await Db.GetJsonAsync<XlObjectRecord>(Keys.CurrentObject(bucketName, key), cancellationToken)
            : await Db.GetJsonAsync<XlObjectRecord>(Keys.Version(bucketName, key, versionId), cancellationToken);
        return record ?? throw new MeansException(MeansErrorCodes.NoSuchKey, "Object does not exist.", 404);
    }

    private async Task<XlObjectManifest> ReadManifestAsync(ObjectInfo info, CancellationToken cancellationToken)
    {
        foreach (var disk in _disks)
        {
            var path = Path.Combine(disk.RootPath, ObjectManifestRelativePath(info.BucketName, info.ObjectId));
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), XlJsonContext.Default.XlObjectManifest)
                    ?? throw new MeansException(MeansErrorCodes.NoSuchKey, "Object manifest is invalid.", 404);
            }
        }

        throw new MeansException(MeansErrorCodes.NoSuchKey, "Object manifest is missing.", 404);
    }

    private async Task QueueHealAsync(ObjectInfo info, string reason, CancellationToken cancellationToken)
    {
        var key = Keys.Heal(info.ObjectId);
        var now = DateTimeOffset.UtcNow;
        var existingBytes = await Db.GetAsync(key, cancellationToken);
        var existing = existingBytes is null ? null : DeserializeHealRecord(existingBytes, now);
        var maxAttemptsReached = existing is not null && existing.AttemptCount >= MaxRepairAttempts;
        var status = maxAttemptsReached
            ? HealStatuses.Failed
            : string.Equals(existing?.Status, HealStatuses.RetryScheduled, StringComparison.Ordinal)
                ? HealStatuses.RetryScheduled
                : HealStatuses.Pending;
        var record = new XlHealRecord(
            info.BucketName,
            info.Key,
            info.ObjectId,
            reason,
            status,
            existing?.AttemptCount ?? 0,
            existing?.QueuedAt ?? now,
            now,
            existing?.LastAttemptAt,
            status == HealStatuses.RetryScheduled ? existing?.NextAttemptAt : null,
            existing?.LastError);
        await Db.PutJsonAsync(key, record, cancellationToken);
    }

    private int MaxRepairAttempts => Math.Max(1, _options.ReplicaRepairMaxAttempts);

    private int MaxRepairConcurrency => Math.Clamp(_options.ReplicaRepairMaxConcurrency, 1, 32);

    private TimeSpan RepairThrottleDelay => TimeSpan.FromMilliseconds(Math.Clamp(_options.ReplicaRepairThrottleDelayMilliseconds, 0, 60_000));

    private int ShardTransferMaxConcurrency => Math.Clamp(_options.ShardTransferMaxConcurrency, 1, 64);

    private long DiskMinAvailableBytesAfterWrite => Math.Max(0, _options.DiskMinAvailableBytesAfterWrite);

    private double DiskMinAvailableRatioAfterWrite => Math.Clamp(_options.DiskMinAvailablePercentAfterWrite, 0, 95) / 100d;

    private int PlacementMinFaultDomains(int replicaCount)
    {
        return Math.Clamp(_options.PlacementMinFaultDomains, 0, Math.Max(1, replicaCount));
    }

    private ObjectPlacementRequest CreatePlacementRequest(
        string bucketName,
        string objectKey,
        string? versionId,
        int replicaCount,
        long contentLength,
        string? poolId = null)
    {
        return new ObjectPlacementRequest(
            bucketName,
            objectKey,
            versionId,
            replicaCount,
            contentLength,
            poolId,
            DiskMinAvailableBytesAfterWrite,
            DiskMinAvailableRatioAfterWrite,
            PlacementMinFaultDomains(replicaCount));
    }

    private void RefreshLocalDiskCapacity()
    {
        if (_disks.Count == 0)
        {
            return;
        }

        _disks = _disks.Select(disk =>
        {
            try
            {
                var drive = DriveInfoFor(disk.RootPath);
                return disk with
                {
                    TotalBytes = Math.Max(0, drive.TotalSize),
                    AvailableBytes = Math.Max(0, drive.AvailableFreeSpace)
                };
            }
            catch
            {
                return disk with
                {
                    Online = false,
                    TotalBytes = 0,
                    AvailableBytes = 0
                };
            }
        }).ToArray();
    }

    private static XlHealRecord DeserializeHealRecord(byte[] value, DateTimeOffset fallbackTimestamp)
    {
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        var bucket = GetJsonString(root, "BucketName", "bucketName", "bucket") ?? string.Empty;
        var key = GetJsonString(root, "Key", "key") ?? string.Empty;
        var objectId = GetJsonString(root, "ObjectId", "objectId") ?? string.Empty;
        var reason = GetJsonString(root, "Reason", "reason") ?? "Unspecified";
        var attemptCount = Math.Max(0, GetJsonInt(root, "AttemptCount", "attemptCount", "attempts") ?? 0);
        return new XlHealRecord(
            bucket,
            key,
            objectId,
            reason,
            NormalizeHealStatus(GetJsonString(root, "Status", "status"), attemptCount),
            attemptCount,
            GetJsonDateTime(root, fallbackTimestamp, "QueuedAt", "queuedAt"),
            GetJsonDateTime(root, fallbackTimestamp, "UpdatedAt", "updatedAt"),
            GetJsonNullableDateTime(root, "LastAttemptAt", "lastAttemptAt"),
            GetJsonNullableDateTime(root, "NextAttemptAt", "nextAttemptAt"),
            TruncateHealError(GetJsonString(root, "LastError", "lastError")));
    }

    private static string NormalizeHealStatus(string? status, int attemptCount)
    {
        if (string.Equals(status, HealStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            return HealStatuses.Failed;
        }

        if (string.Equals(status, HealStatuses.RetryScheduled, StringComparison.OrdinalIgnoreCase))
        {
            return HealStatuses.RetryScheduled;
        }

        return attemptCount > 0 && !string.IsNullOrWhiteSpace(status)
            ? HealStatuses.RetryScheduled
            : HealStatuses.Pending;
    }

    private static string? GetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? GetJsonInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return null;
    }

    private static DateTimeOffset GetJsonDateTime(
        JsonElement element,
        DateTimeOffset fallback,
        params string[] names)
    {
        return GetJsonNullableDateTime(element, names) ?? fallback;
    }

    private static DateTimeOffset? GetJsonNullableDateTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static string? TruncateHealError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return error.Length <= 512 ? error : error[..512];
    }

    private static class HealStatuses
    {
        public const string Pending = "Pending";
        public const string RetryScheduled = "RetryScheduled";
        public const string Failed = "Failed";
    }

    private static ObjectInfo ToObjectInfo(XlObjectRecord record)
    {
        return new ObjectInfo(
            record.BucketName,
            record.Key,
            record.ObjectId,
            record.ETag,
            record.ContentLength,
            record.ContentType,
            record.LastModified,
            record.Metadata,
            record.CacheControl,
            record.ContentDisposition);
    }

    private ErasureCodingProfile DefaultEcProfile()
    {
        return new ErasureCodingProfile(
            "xlfs-default",
            Math.Max(1, _options.ErasureDataShards),
            Math.Max(0, _options.ErasureParityShards),
            128 * 1024,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private int WriteQuorum => _options.WriteQuorum > 0 ? _options.WriteQuorum : Math.Max(1, _options.ErasureDataShards);

    private int ReadQuorum => _options.ReadQuorum > 0 ? _options.ReadQuorum : 1;

    private MeansLogDb Db => _db ?? throw new InvalidOperationException("XlFs store is not initialized.");

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        return metadata.ToDictionary(item => item.Key.ToLowerInvariant(), item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeVersioningStatus(string status)
    {
        if (string.Equals(status, BucketVersioningStatuses.Enabled, StringComparison.OrdinalIgnoreCase))
        {
            return BucketVersioningStatuses.Enabled;
        }

        return string.Equals(status, BucketVersioningStatuses.Suspended, StringComparison.OrdinalIgnoreCase)
            ? BucketVersioningStatuses.Suspended
            : BucketVersioningStatuses.Off;
    }

    private string TempPath(string fileName)
    {
        return Path.Combine(_disks.Count > 0 ? _disks[0].RootPath : _options.ObjectsPath, ".means.sys", "tmp", fileName);
    }

    private static string ObjectRelativePath(string bucketName, string objectId, int setIndex)
    {
        return Path.Combine("objects", BucketHash(bucketName), objectId, "part." + setIndex.ToString("D2"));
    }

    private static string ObjectManifestRelativePath(string bucketName, string objectId)
    {
        return Path.Combine("objects", BucketHash(bucketName), objectId, "xl.meta");
    }

    private static string BucketHash(string bucketName)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bucketName))).ToLowerInvariant()[..8];
    }

    private static string Escape(string value)
    {
        return Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
    }

    private static string Unescape(string value)
    {
        return Encoding.UTF8.GetString(Convert.FromHexString(value));
    }

    private static string? EncodeToken(string key) => Escape(key);

    private static string? DecodeToken(string? token)
    {
        return string.IsNullOrWhiteSpace(token) ? null : Unescape(token);
    }

    private static string NormalizeEtag(string etag) => etag.Trim().Trim('"').ToLowerInvariant();

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, XlJson.TypeInfo<T>());

    private static T Deserialize<T>(byte[] value)
    {
        return JsonSerializer.Deserialize(value, XlJson.TypeInfo<T>())
            ?? throw new InvalidOperationException("Stored XlFs metadata is invalid.");
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static DriveInfo DriveInfoFor(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return new DriveInfo(string.IsNullOrWhiteSpace(root) ? path : root);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record XlDisk(string DiskId, string RootPath, int SetIndex, bool Online, long TotalBytes, long AvailableBytes);

    private sealed class NoopClusterShardTransport : IClusterShardTransport
    {
        public static readonly NoopClusterShardTransport Instance = new();

        public bool Enabled => false;

        public Task<ClusterShardWriteResult> WriteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<ClusterShardReadResult> OpenShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<ClusterShardStatResult> StatShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<bool> DeleteShardAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<ClusterShardWriteResult> WriteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            Stream content,
            long expectedLength,
            string expectedChecksumSha256,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<ClusterShardReadResult> OpenManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<ClusterShardStatResult> StatManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        public Task<bool> DeleteManifestAsync(
            ClusterNodeInfo node,
            string diskId,
            string relativePath,
            CancellationToken cancellationToken)
        {
            throw NotConfigured();
        }

        private static MeansException NotConfigured()
        {
            return new MeansException(MeansErrorCodes.InvalidRequest, "Cluster shard transport is not configured.", 503);
        }
    }
}
