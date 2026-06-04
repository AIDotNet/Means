using System.Buffers;
using System.Security.Cryptography;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    public async Task<ClusterShardWriteResult> WriteShardAsync(
        string diskId,
        string relativePath,
        Stream content,
        long? expectedLength,
        string? expectedChecksumSha256,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (expectedLength is < 0)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid shard length.", 400);
        }

        if (expectedLength is not null && expectedLength > maxBytes)
        {
            throw new MeansException(MeansErrorCodes.EntityTooLarge, "Shard exceeds the configured transfer limit.", 413);
        }

        var (disk, targetPath, normalizedRelativePath) = ResolveShardTarget(diskId, relativePath);
        var tempPath = Path.Combine(disk.RootPath, ".means.sys", "tmp", "remote-shards", Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            var (length, checksum) = await WriteShardTempFileAsync(
                content,
                tempPath,
                maxBytes,
                cancellationToken);
            if (expectedLength is not null && length != expectedLength)
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard length mismatch.", 400);
            }

            if (!string.IsNullOrWhiteSpace(expectedChecksumSha256)
                && !string.Equals(checksum, expectedChecksumSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard checksum mismatch.", 400);
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return new ClusterShardWriteResult(disk.DiskId, normalizedRelativePath, length, checksum);
        }
        finally
        {
            DeleteFileQuietly(tempPath);
        }
    }

    public async Task<ClusterShardReadResult> OpenShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var (disk, targetPath, normalizedRelativePath) = ResolveShardTarget(diskId, relativePath);
        if (!File.Exists(targetPath))
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Shard does not exist.", 404);
        }

        var file = new FileInfo(targetPath);
        return new ClusterShardReadResult(
            disk.DiskId,
            normalizedRelativePath,
            file.Length,
            new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan),
            targetPath);
    }

    public Task<ClusterShardStatResult> StatShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return StatClusterFileAsync(ResolveShardTarget, diskId, relativePath, "Shard does not exist.", cancellationToken);
    }

    public async Task<bool> DeleteShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var (_, targetPath, _) = ResolveShardTarget(diskId, relativePath);
        if (!File.Exists(targetPath))
        {
            return false;
        }

        File.Delete(targetPath);
        return true;
    }

    public async Task<ClusterShardWriteResult> WriteManifestAsync(
        string diskId,
        string relativePath,
        Stream content,
        long? expectedLength,
        string? expectedChecksumSha256,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (expectedLength is < 0)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid manifest length.", 400);
        }

        if (expectedLength is not null && expectedLength > maxBytes)
        {
            throw new MeansException(MeansErrorCodes.EntityTooLarge, "Manifest exceeds the configured transfer limit.", 413);
        }

        var (disk, targetPath, normalizedRelativePath) = ResolveManifestTarget(diskId, relativePath);
        var tempPath = Path.Combine(disk.RootPath, ".means.sys", "tmp", "remote-manifests", Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            var (length, checksum) = await WriteShardTempFileAsync(
                content,
                tempPath,
                maxBytes,
                cancellationToken);
            if (expectedLength is not null && length != expectedLength)
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, "Manifest length mismatch.", 400);
            }

            if (!string.IsNullOrWhiteSpace(expectedChecksumSha256)
                && !string.Equals(checksum, expectedChecksumSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, "Manifest checksum mismatch.", 400);
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return new ClusterShardWriteResult(disk.DiskId, normalizedRelativePath, length, checksum);
        }
        finally
        {
            DeleteFileQuietly(tempPath);
        }
    }

    public async Task<ClusterShardReadResult> OpenManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var (disk, targetPath, normalizedRelativePath) = ResolveManifestTarget(diskId, relativePath);
        if (!File.Exists(targetPath))
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Manifest does not exist.", 404);
        }

        var file = new FileInfo(targetPath);
        return new ClusterShardReadResult(
            disk.DiskId,
            normalizedRelativePath,
            file.Length,
            new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan),
            targetPath);
    }

    public Task<ClusterShardStatResult> StatManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return StatClusterFileAsync(ResolveManifestTarget, diskId, relativePath, "Manifest does not exist.", cancellationToken);
    }

    public async Task<bool> DeleteManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var (_, targetPath, _) = ResolveManifestTarget(diskId, relativePath);
        if (!File.Exists(targetPath))
        {
            return false;
        }

        File.Delete(targetPath);
        return true;
    }

    private async Task<ClusterShardStatResult> StatClusterFileAsync(
        Func<string, string, (XlDisk Disk, string TargetPath, string NormalizedRelativePath)> resolveTarget,
        string diskId,
        string relativePath,
        string missingMessage,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var (disk, targetPath, normalizedRelativePath) = resolveTarget(diskId, relativePath);
        if (!File.Exists(targetPath))
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, missingMessage, 404);
        }

        var file = new FileInfo(targetPath);
        return new ClusterShardStatResult(
            disk.DiskId,
            normalizedRelativePath,
            file.Length,
            await ComputeFileSha256Async(targetPath, cancellationToken));
    }

    private async Task<(long Length, string ChecksumSha256)> WriteShardTempFileAsync(
        Stream content,
        string tempPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(XlStreamBufferSize);
        long length = 0;
        try
        {
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                length += read;
                if (length > maxBytes)
                {
                    throw new MeansException(MeansErrorCodes.EntityTooLarge, "Shard exceeds the configured transfer limit.", 413);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha256.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (length, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private (XlDisk Disk, string TargetPath, string NormalizedRelativePath) ResolveShardTarget(
        string diskId,
        string relativePath)
    {
        return ResolveClusterFileTarget(diskId, NormalizeShardRelativePath(relativePath));
    }

    private (XlDisk Disk, string TargetPath, string NormalizedRelativePath) ResolveManifestTarget(
        string diskId,
        string relativePath)
    {
        return ResolveClusterFileTarget(diskId, NormalizeManifestRelativePath(relativePath));
    }

    private (XlDisk Disk, string TargetPath, string NormalizedRelativePath) ResolveClusterFileTarget(
        string diskId,
        string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(diskId))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Disk id is required.", 400);
        }

        var disk = _disks.FirstOrDefault(item => string.Equals(item.DiskId, diskId.Trim(), StringComparison.Ordinal))
            ?? throw new MeansException(MeansErrorCodes.InvalidArgument, "Disk is not registered on this node.", 404);
        var targetPath = Path.GetFullPath(Path.Combine(disk.RootPath, normalizedRelativePath));
        var diskRoot = Path.GetFullPath(disk.RootPath);
        if (!targetPath.StartsWith(diskRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard path escapes the disk root.", 400);
        }

        return (disk, targetPath, normalizedRelativePath);
    }

    private static string NormalizeShardRelativePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard path must be relative.", 400);
        }

        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard path cannot contain traversal segments.", 400);
        }

        if (!normalized.StartsWith("objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(normalized), "xl.meta", StringComparison.OrdinalIgnoreCase))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Shard path must target an object data file.", 400);
        }

        return normalized;
    }

    private static string NormalizeManifestRelativePath(string relativePath)
    {
        var normalized = NormalizeClusterRelativePath(relativePath);
        if (!normalized.StartsWith("objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(normalized), "xl.meta", StringComparison.OrdinalIgnoreCase))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Manifest path must target an object xl.meta file.", 400);
        }

        return normalized;
    }

    private static string NormalizeClusterRelativePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Cluster file path must be relative.", 400);
        }

        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Cluster file path cannot contain traversal segments.", 400);
        }

        return normalized;
    }
}
