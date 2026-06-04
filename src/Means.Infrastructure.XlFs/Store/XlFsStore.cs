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
    IErasureCodingProfileStore,
    IMetadataMaintenanceStore,
    IStorageMaintenanceOperations,
    IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const long MinimumMultipartPartSize = 5L * 1024 * 1024;
    private readonly XlFsOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private MeansLogDb? _db;
    private IReadOnlyList<XlDisk> _disks = [];
    private long _auditId;

    public XlFsStore(IOptions<XlFsOptions> options)
    {
        _options = options.Value;
        _options.ObjectsPath = ResolvePath(_options.ObjectsPath);
        _options.DatabasePath = ResolvePath(_options.DatabasePath);
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
            if (!_options.AllowNewFormatWithExistingSqlite
                && File.Exists(_options.DatabasePath)
                && roots.All(root => !File.Exists(Path.Combine(root, ".means.sys", "format.json"))))
            {
                throw new InvalidOperationException(
                    "XlFs detected an existing SQLite metadata file but no XlFs disk format. "
                    + "Automatic SQLite migration is not supported; export/import manually or set Means:Storage:AllowNewFormatWithExistingSqlite=true to intentionally start a new XlFs namespace.");
            }

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
        await Db.PutJsonAsync(Keys.Heal(info.ObjectId), new Dictionary<string, string>
        {
            ["bucket"] = info.BucketName,
            ["key"] = info.Key,
            ["objectId"] = info.ObjectId,
            ["reason"] = reason,
            ["queuedAt"] = DateTimeOffset.UtcNow.ToString("O")
        }, cancellationToken);
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
}
