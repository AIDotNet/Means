using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<StorageGarbageCollectionResult> CollectStorageGarbageAsync(
        bool delete,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var candidateLimit = Math.Clamp(maxFiles, 1, 100_000);
        var tempCutoffUtc = DateTimeOffset.UtcNow.Subtract(
            TimeSpan.FromMinutes(Math.Max(1, _options.GarbageCollectionTempFileAgeMinutes)));
        var referencedPaths = await ReadReferencedStoragePathsAsync(cancellationToken);
        var storageRoots = await ReadRegisteredStorageRootsAsync(cancellationToken);

        long scanned = 0;
        long deleted = 0;
        long failedDeletes = 0;
        long orphanedReplicaFiles = 0;
        long orphanedFallbackFiles = 0;
        long orphanedMultipartFiles = 0;
        long orphanedEcShardFiles = 0;
        long expiredTempFiles = 0;
        var limitReached = false;

        void ProbeUnreferencedFile(string path, StorageGarbageFileKind kind)
        {
            if (limitReached)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            var fullPath = NormalizeStoragePath(path);
            if (referencedPaths.Contains(fullPath))
            {
                return;
            }

            if (!IsOlderThan(fullPath, tempCutoffUtc))
            {
                return;
            }

            AddCandidate(fullPath, kind);
        }

        void ProbeTempFile(string path)
        {
            if (limitReached)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            var fullPath = NormalizeStoragePath(path);
            if (!IsOlderThan(fullPath, tempCutoffUtc))
            {
                return;
            }

            AddCandidate(fullPath, StorageGarbageFileKind.Temp);
        }

        void AddCandidate(string fullPath, StorageGarbageFileKind kind)
        {
            switch (kind)
            {
                case StorageGarbageFileKind.ObjectReplica:
                    orphanedReplicaFiles++;
                    break;
                case StorageGarbageFileKind.FallbackObject:
                    orphanedFallbackFiles++;
                    break;
                case StorageGarbageFileKind.MultipartPart:
                    orphanedMultipartFiles++;
                    break;
                case StorageGarbageFileKind.ErasureCodingShard:
                    orphanedEcShardFiles++;
                    break;
                case StorageGarbageFileKind.Temp:
                    expiredTempFiles++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            if (delete)
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        deleted++;
                    }
                }
                catch
                {
                    failedDeletes++;
                }
            }

            if (orphanedReplicaFiles
                + orphanedFallbackFiles
                + orphanedMultipartFiles
                + orphanedEcShardFiles
                + expiredTempFiles >= candidateLimit)
            {
                limitReached = true;
            }
        }

        foreach (var root in storageRoots)
        {
            ScanObjectShardFiles(root, IsSamePath(root, _options.ObjectsPath)
                ? StorageGarbageFileKind.FallbackObject
                : StorageGarbageFileKind.ObjectReplica);
            if (limitReached)
            {
                break;
            }
        }

        if (!limitReached)
        {
            ScanMultipartPartFiles(Path.Combine(_options.ObjectsPath, "multipart"));
        }

        if (!limitReached)
        {
            foreach (var root in storageRoots)
            {
                ScanErasureCodingFiles(Path.Combine(root, "ec"));
                if (limitReached)
                {
                    break;
                }
            }
        }

        if (!limitReached)
        {
            ScanTempFiles(Path.Combine(_options.ObjectsPath, "tmp"), recursive: false);
        }

        if (!limitReached)
        {
            foreach (var root in storageRoots)
            {
                ScanHealthProbeTempFiles(root);
                if (limitReached)
                {
                    break;
                }
            }
        }

        return new StorageGarbageCollectionResult(
            delete,
            limitReached,
            scanned,
            orphanedReplicaFiles + orphanedFallbackFiles + orphanedMultipartFiles + orphanedEcShardFiles + expiredTempFiles,
            deleted,
            failedDeletes,
            orphanedReplicaFiles,
            orphanedFallbackFiles,
            orphanedMultipartFiles,
            orphanedEcShardFiles,
            expiredTempFiles);

        void ScanObjectShardFiles(string root, StorageGarbageFileKind kind)
        {
            foreach (var shardDirectory in EnumerateDirectoriesSafe(root))
            {
                if (limitReached)
                {
                    return;
                }

                var shardName = Path.GetFileName(shardDirectory);
                if (!IsTwoCharacterShard(shardName))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafe(shardDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (limitReached)
                    {
                        return;
                    }

                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeTempFile(file);
                        continue;
                    }

                    ProbeUnreferencedFile(file, kind);
                }
            }
        }

        void ScanMultipartPartFiles(string multipartRoot)
        {
            foreach (var shardDirectory in EnumerateDirectoriesSafe(multipartRoot))
            {
                if (limitReached)
                {
                    return;
                }

                var shardName = Path.GetFileName(shardDirectory);
                if (!IsTwoCharacterShard(shardName))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafe(shardDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (limitReached)
                    {
                        return;
                    }

                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeTempFile(file);
                        continue;
                    }

                    ProbeUnreferencedFile(file, StorageGarbageFileKind.MultipartPart);
                }
            }
        }

        void ScanErasureCodingFiles(string ecRoot)
        {
            foreach (var shardDirectory in EnumerateDirectoriesSafe(ecRoot))
            {
                if (limitReached)
                {
                    return;
                }

                var shardName = Path.GetFileName(shardDirectory);
                if (!IsTwoCharacterShard(shardName))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafe(shardDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (limitReached)
                    {
                        return;
                    }

                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeTempFile(file);
                    }
                    else if (file.EndsWith(".ec", StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeUnreferencedFile(file, StorageGarbageFileKind.ErasureCodingShard);
                    }
                }
            }
        }

        void ScanTempFiles(string tempRoot, bool recursive)
        {
            foreach (var file in EnumerateFilesSafe(
                tempRoot,
                "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (limitReached)
                {
                    return;
                }

                ProbeTempFile(file);
            }
        }

        void ScanHealthProbeTempFiles(string root)
        {
            foreach (var file in EnumerateFilesSafe(root, ".means-disk-health-*.tmp", SearchOption.TopDirectoryOnly))
            {
                if (limitReached)
                {
                    return;
                }

                ProbeTempFile(file);
            }
        }
    }

    private async Task<HashSet<string>> ReadReferencedStoragePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StoragePathComparer);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await AddReferencedContentPathsAsync(connection, paths, "select content_path from object_replicas;", cancellationToken);
        await AddReferencedContentPathsAsync(connection, paths, "select content_path from object_ec_shards;", cancellationToken);

        await using (var objectIds = connection.CreateCommand())
        {
            objectIds.CommandText = """
                select object_id from objects
                union
                select object_id from object_versions where is_delete_marker = 0;
                """;
            await using var reader = await objectIds.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var objectId = reader.GetString(0);
                if (objectId.Length >= 2)
                {
                    paths.Add(NormalizeStoragePath(GetObjectPath(objectId)));
                }
            }
        }

        await using (var partIds = connection.CreateCommand())
        {
            partIds.CommandText = "select part_id from multipart_parts;";
            await using var reader = await partIds.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var partId = reader.GetString(0);
                if (partId.Length >= 2)
                {
                    paths.Add(NormalizeStoragePath(GetMultipartPartPath(partId)));
                }
            }
        }

        return paths;
    }

    private async Task<IReadOnlyList<string>> ReadRegisteredStorageRootsAsync(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StoragePathComparer)
        {
            NormalizeStoragePath(_options.ObjectsPath)
        };

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select mount_path from storage_disks order by mount_path;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mountPath = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(mountPath))
            {
                roots.Add(NormalizeStoragePath(mountPath));
            }
        }

        return roots.ToArray();
    }

    private static async Task AddReferencedContentPathsAsync(
        SqliteConnection connection,
        HashSet<string> paths,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                paths.Add(NormalizeStoragePath(reader.GetString(0)));
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch
                {
                    break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string path, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(path, searchPattern, searchOption).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch
                {
                    break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static bool IsTwoCharacterShard(string value)
    {
        return value.Length == 2 && value.All(Uri.IsHexDigit);
    }

    private static bool IsOlderThan(string path, DateTimeOffset cutoffUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < cutoffUtc.UtcDateTime;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(NormalizeStoragePath(left), NormalizeStoragePath(right), StoragePathComparison);
    }

    private static string NormalizeStoragePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static StringComparer StoragePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison StoragePathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private enum StorageGarbageFileKind
    {
        ObjectReplica,
        FallbackObject,
        MultipartPart,
        ErasureCodingShard,
        Temp
    }
}
