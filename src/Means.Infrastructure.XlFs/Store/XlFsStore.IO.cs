using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    private const int XlStreamBufferSize = 1024 * 1024;
    private const string FullCopyAlgorithm = "full-copy-v1";
    private const string ReedSolomonAlgorithm = "reed-solomon-v1";
    private static readonly byte[] GfExp = CreateGfTables().Exp;
    private static readonly byte[] GfLog = CreateGfTables().Log;

    private async Task<XlErasureWriteResult?> TryWriteErasureCodedShardsAsync(
        string bucketName,
        string objectId,
        string sourcePath,
        long length,
        CancellationToken cancellationToken)
    {
        var dataShards = Math.Max(1, _options.ErasureDataShards);
        var parityShards = Math.Max(0, _options.ErasureParityShards);
        var totalShards = dataShards + parityShards;
        if (dataShards <= 1 || parityShards <= 0)
        {
            return null;
        }

        var shardLength = CalculateErasureShardLength(length, dataShards);
        var targets = await TryPlanErasureShardTargetsAsync(
                bucketName,
                objectId,
                totalShards,
                shardLength,
                cancellationToken)
            ?? PlanLocalErasureShardTargets(bucketName, objectId, totalShards);
        if (targets.Count < totalShards)
        {
            return null;
        }

        var prepared = new List<PreparedShard>(totalShards);
        try
        {
            await WriteErasureDataShardsAsync(
                bucketName,
                objectId,
                sourcePath,
                targets.Take(dataShards).ToArray(),
                length,
                shardLength,
                prepared,
                cancellationToken);

            await WriteErasureParityShardsAsync(
                bucketName,
                objectId,
                targets.Skip(dataShards).ToArray(),
                prepared.Take(dataShards).ToArray(),
                shardLength,
                prepared,
                cancellationToken);

            await UploadRemotePreparedShardsAsync(prepared, cancellationToken);
            var committedShards = prepared.Where(IsPreparedShardCommitted).ToArray();
            if (committedShards.Length < ErasureWriteQuorum(dataShards, parityShards)
                || !CanRecoverErasureData(dataShards, committedShards.Select(shard => shard.Manifest.SetIndex)))
            {
                await DeletePreparedShardsQuietlyAsync(prepared, cancellationToken);
                throw new MeansException(MeansErrorCodes.SlowDown, "Insufficient EC shard write quorum.", 503);
            }
        }
        catch
        {
            await DeletePreparedShardsQuietlyAsync(prepared, cancellationToken);
            throw;
        }
        finally
        {
            DeleteRemoteStagingFilesQuietly(prepared);
        }

        var created = prepared
            .Select(shard => shard.Manifest)
            .OrderBy(shard => shard.SetIndex)
            .ToArray();

        return new XlErasureWriteResult(
            new XlErasureInfo(
                ReedSolomonAlgorithm,
                dataShards,
                parityShards,
                ErasureCellSizeBytes,
                ErasureWriteQuorum(dataShards, parityShards),
                dataShards),
            created,
            prepared.Count(shard => !IsPreparedShardCommitted(shard)));
    }

    private async Task WriteErasureDataShardsAsync(
        string bucketName,
        string objectId,
        string sourcePath,
        IReadOnlyList<ShardWriteTarget> targets,
        long contentLength,
        long shardLength,
        List<PreparedShard> prepared,
        CancellationToken cancellationToken)
    {
        var bufferSize = ErasureBufferSize;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var zeroBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        Array.Clear(zeroBuffer, 0, zeroBuffer.Length);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var totalRead = 0L;
        try
        {
            for (var shardIndex = 0; shardIndex < targets.Count; shardIndex++)
            {
                var target = targets[shardIndex];
                var relative = ObjectShardRelativePath(bucketName, objectId, shardIndex);
                var localPath = target.LocalDisk is { } disk
                    ? Path.Combine(disk.RootPath, relative)
                    : RemoteShardStagingPath(objectId, shardIndex);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    using var sha256 = SHA256.Create();
                    await using var output = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var remainingShardBytes = shardLength;
                    while (remainingShardBytes > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var toProcess = (int)Math.Min(buffer.Length, remainingShardBytes);
                        var remainingSourceBytes = contentLength - totalRead;
                        var toRead = (int)Math.Min(toProcess, Math.Max(0, remainingSourceBytes));
                        var read = 0;
                        if (toRead > 0)
                        {
                            read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                            if (read <= 0)
                            {
                                throw new EndOfStreamException("Object source ended before EC data shards were complete.");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            sha256.TransformBlock(buffer, 0, read, null, 0);
                            totalRead += read;
                        }

                        if (read < toProcess)
                        {
                            var padding = toProcess - read;
                            await output.WriteAsync(zeroBuffer.AsMemory(0, padding), cancellationToken);
                            sha256.TransformBlock(zeroBuffer, 0, padding, null, 0);
                        }

                        remainingShardBytes -= toProcess;
                    }

                    sha256.TransformFinalBlock([], 0, 0);
                    prepared.Add(new PreparedShard(
                        target,
                        new XlShardManifest(
                            target.DiskId,
                            shardIndex,
                            relative,
                            shardLength,
                            Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant()),
                        localPath,
                        target.LocalDisk is null));
                }
                catch
                {
                    DeleteFileQuietly(localPath);
                    throw;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeroBuffer);
        }
    }

    private async Task WriteErasureParityShardsAsync(
        string bucketName,
        string objectId,
        IReadOnlyList<ShardWriteTarget> targets,
        IReadOnlyList<PreparedShard> dataShards,
        long shardLength,
        List<PreparedShard> prepared,
        CancellationToken cancellationToken)
    {
        var bufferSize = ErasureBufferSize;
        var parityBuffers = targets.Select(_ => ArrayPool<byte>.Shared.Rent(bufferSize)).ToArray();
        var readBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var dataStreams = new List<FileStream>(dataShards.Count);
        var parityStreams = new List<FileStream>(targets.Count);
        var parityHashes = new List<SHA256>(targets.Count);
        var parityPrepared = new List<PreparedShard>(targets.Count);
        var completed = false;
        try
        {
            foreach (var shard in dataShards.OrderBy(shard => shard.Manifest.SetIndex))
            {
                dataStreams.Add(new FileStream(
                    shard.LocalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            for (var parityIndex = 0; parityIndex < targets.Count; parityIndex++)
            {
                var target = targets[parityIndex];
                var shardIndex = dataShards.Count + parityIndex;
                var relative = ObjectShardRelativePath(bucketName, objectId, shardIndex);
                var localPath = target.LocalDisk is { } disk
                    ? Path.Combine(disk.RootPath, relative)
                    : RemoteShardStagingPath(objectId, shardIndex);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                parityStreams.Add(new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
                parityHashes.Add(SHA256.Create());
                parityPrepared.Add(new PreparedShard(
                    target,
                    new XlShardManifest(
                        target.DiskId,
                        shardIndex,
                        relative,
                        shardLength,
                        string.Empty),
                    localPath,
                    target.LocalDisk is null));
                }

            var remaining = shardLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toProcess = (int)Math.Min(readBuffer.Length, remaining);
                foreach (var parityBuffer in parityBuffers)
                {
                    Array.Clear(parityBuffer, 0, toProcess);
                }

                for (var dataIndex = 0; dataIndex < dataStreams.Count; dataIndex++)
                {
                    await dataStreams[dataIndex].ReadExactlyAsync(readBuffer.AsMemory(0, toProcess), cancellationToken);
                    for (var parityIndex = 0; parityIndex < parityStreams.Count; parityIndex++)
                    {
                        XorMultiply(parityBuffers[parityIndex], readBuffer, ParityCoefficient(parityIndex, dataIndex), toProcess);
                    }
                }

                for (var parityIndex = 0; parityIndex < parityStreams.Count; parityIndex++)
                {
                    await parityStreams[parityIndex].WriteAsync(parityBuffers[parityIndex].AsMemory(0, toProcess), cancellationToken);
                    parityHashes[parityIndex].TransformBlock(parityBuffers[parityIndex], 0, toProcess, null, 0);
                }

                remaining -= toProcess;
            }

            for (var parityIndex = 0; parityIndex < targets.Count; parityIndex++)
            {
                parityHashes[parityIndex].TransformFinalBlock([], 0, 0);
                var shardIndex = dataShards.Count + parityIndex;
                parityPrepared[parityIndex].Manifest = parityPrepared[parityIndex].Manifest with
                {
                    ChecksumSha256 = Convert.ToHexString(parityHashes[parityIndex].Hash ?? []).ToLowerInvariant()
                };
            }

            prepared.AddRange(parityPrepared);
            completed = true;
        }
        finally
        {
            foreach (var stream in dataStreams)
            {
                await stream.DisposeAsync();
            }

            foreach (var stream in parityStreams)
            {
                await stream.DisposeAsync();
            }

            foreach (var hash in parityHashes)
            {
                hash.Dispose();
            }

            foreach (var parityBuffer in parityBuffers)
            {
                ArrayPool<byte>.Shared.Return(parityBuffer);
            }

            ArrayPool<byte>.Shared.Return(readBuffer);

            if (!completed)
            {
                foreach (var shard in parityPrepared)
                {
                    DeleteFileQuietly(shard.LocalPath);
                }
            }
        }
    }

    private async Task<IReadOnlyList<ShardWriteTarget>?> TryPlanErasureShardTargetsAsync(
        string bucketName,
        string objectId,
        int totalShards,
        long shardLength,
        CancellationToken cancellationToken)
    {
        try
        {
            var topology = await GetClusterTopologyAsync(
                DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(60)),
                cancellationToken);
            var plan = _placementPlanner.PlanPlacement(
                CreatePlacementRequest(
                    bucketName,
                    objectId,
                    objectId,
                    totalShards,
                    shardLength),
                topology);
            var nodesById = topology.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            var selectedDisks = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<ShardWriteTarget>(totalShards);
            foreach (var replica in plan.Replicas.OrderBy(replica => replica.ReplicaIndex))
            {
                if (!selectedDisks.Add(replica.DiskId))
                {
                    return null;
                }

                var localDisk = _disks.FirstOrDefault(disk => string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal));
                if (localDisk is not null)
                {
                    if (!localDisk.Online)
                    {
                        return null;
                    }

                    targets.Add(new ShardWriteTarget(replica.DiskId, localDisk, null));
                    continue;
                }

                if (!_shardTransport.Enabled
                    || !nodesById.TryGetValue(replica.NodeId, out var node)
                    || !string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                {
                    return null;
                }

                var remoteDisk = node.Disks.FirstOrDefault(disk =>
                    string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal)
                    && string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
                if (remoteDisk is null)
                {
                    return null;
                }

                targets.Add(new ShardWriteTarget(replica.DiskId, null, node));
            }

            return targets.Count == totalShards ? targets : null;
        }
        catch (MeansException)
        {
            return null;
        }
    }

    private IReadOnlyList<ShardWriteTarget> PlanLocalErasureShardTargets(
        string bucketName,
        string objectId,
        int totalShards)
    {
        return _disks
            .Where(disk => disk.Online)
            .OrderBy(disk => ErasureDiskScore(bucketName, objectId, disk), StringComparer.Ordinal)
            .ThenBy(disk => disk.SetIndex)
            .Take(totalShards)
            .Select(disk => new ShardWriteTarget(disk.DiskId, disk, null))
            .ToArray();
    }

    private async Task UploadRemotePreparedShardsAsync(
        IReadOnlyList<PreparedShard> prepared,
        CancellationToken cancellationToken)
    {
        var remoteShards = prepared
            .Where(shard => shard.Target.RemoteNode is not null)
            .ToArray();
        await Parallel.ForEachAsync(
            remoteShards,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ShardTransferMaxConcurrency
            },
            async (shard, token) =>
            {
                try
                {
                    await using var content = new FileStream(
                        shard.LocalPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        XlStreamBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await _shardTransport.WriteShardAsync(
                        shard.Target.RemoteNode!,
                        shard.Target.DiskId,
                        shard.Manifest.RelativePath,
                        content,
                        shard.Manifest.Size,
                        shard.Manifest.ChecksumSha256,
                        token);
                    shard.RemoteUploaded = true;
                }
                catch when (!token.IsCancellationRequested)
                {
                }
            });
    }

    private async Task DeletePreparedShardsQuietlyAsync(
        IReadOnlyList<PreparedShard> prepared,
        CancellationToken cancellationToken)
    {
        foreach (var shard in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shard.Target.LocalDisk is not null)
            {
                DeleteFileQuietly(shard.LocalPath);
                continue;
            }

            if (shard.RemoteUploaded && shard.Target.RemoteNode is not null)
            {
                try
                {
                    await _shardTransport.DeleteShardAsync(
                        shard.Target.RemoteNode,
                        shard.Target.DiskId,
                        shard.Manifest.RelativePath,
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static void DeleteRemoteStagingFilesQuietly(IReadOnlyList<PreparedShard> prepared)
    {
        foreach (var shard in prepared.Where(shard => shard.DeleteLocalAfterUpload))
        {
            DeleteFileQuietly(shard.LocalPath);
        }
    }

    private async Task<IReadOnlyList<XlShardManifest>> WriteFullCopyShardsAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        string sourcePath,
        Func<int, string> relativePathFactory,
        long length,
        string checksum,
        string quorumErrorMessage,
        CancellationToken cancellationToken)
    {
        var targets = await TryPlanFullCopyTargetsAsync(
                bucketName,
                objectKey,
                versionId,
                length,
                cancellationToken)
            ?? PlanLocalFullCopyTargets();
        if (targets.Count < WriteQuorum)
        {
            throw new MeansException(MeansErrorCodes.SlowDown, quorumErrorMessage, 503);
        }

        var shards = new ConcurrentBag<XlShardManifest>();
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ShardTransferMaxConcurrency
            },
            async (target, token) =>
            {
                var relative = relativePathFactory(target.SetIndex);
                var shard = new XlShardManifest(target.DiskId, target.SetIndex, relative, length, checksum);
                try
                {
                    if (target.LocalDisk is { } disk)
                    {
                        var path = Path.Combine(disk.RootPath, relative);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await source.CopyToAsync(output, XlStreamBufferSize, token);
                        }
                        catch
                        {
                            DeleteFileQuietly(path);
                            throw;
                        }
                    }
                    else
                    {
                        if (target.RemoteNode is null)
                        {
                            throw new MeansException(MeansErrorCodes.InvalidRequest, "Remote full-copy target is not online.", 503);
                        }

                        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await _shardTransport.WriteShardAsync(
                            target.RemoteNode,
                            target.DiskId,
                            relative,
                            source,
                            length,
                            checksum,
                            token);
                    }

                    shards.Add(shard);
                }
                catch when (!token.IsCancellationRequested)
                {
                }
            });

        var committed = shards.OrderBy(shard => shard.SetIndex).ToArray();
        if (committed.Length >= WriteQuorum)
        {
            return committed;
        }

        await DeleteShardCopiesQuietlyAsync(committed, CancellationToken.None);
        throw new MeansException(MeansErrorCodes.SlowDown, quorumErrorMessage, 503);
    }

    private async Task<IReadOnlyList<FullCopyWriteTarget>?> TryPlanFullCopyTargetsAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        long contentLength,
        CancellationToken cancellationToken)
    {
        if (!_shardTransport.Enabled)
        {
            return null;
        }

        try
        {
            var topology = await GetClusterTopologyAsync(
                DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(60)),
                cancellationToken);
            var localDiskIds = _disks.Select(disk => disk.DiskId).ToHashSet(StringComparer.Ordinal);
            var onlineRemoteDiskCount = topology.Nodes
                .Where(node => string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                .SelectMany(node => node.Disks)
                .Count(disk =>
                    !localDiskIds.Contains(disk.DiskId)
                    && string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
            if (onlineRemoteDiskCount == 0)
            {
                return null;
            }

            var onlineDiskCount = topology.Nodes
                .Where(node => string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                .SelectMany(node => node.Disks)
                .Count(disk => string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
            var desiredReplicas = Math.Min(
                Math.Min(onlineDiskCount, 16),
                Math.Max(WriteQuorum, _disks.Count(disk => disk.Online)));
            if (desiredReplicas < WriteQuorum)
            {
                return null;
            }

            var plan = _placementPlanner.PlanPlacement(
                CreatePlacementRequest(
                    bucketName,
                    objectKey,
                    versionId,
                    desiredReplicas,
                    contentLength),
                topology);
            var nodesById = topology.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            var selectedDisks = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<FullCopyWriteTarget>(desiredReplicas);
            foreach (var replica in plan.Replicas.OrderBy(replica => replica.ReplicaIndex))
            {
                if (!selectedDisks.Add(replica.DiskId))
                {
                    return null;
                }

                var localDisk = _disks.FirstOrDefault(disk => string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal));
                if (localDisk is not null)
                {
                    if (!localDisk.Online)
                    {
                        return null;
                    }

                    targets.Add(new FullCopyWriteTarget(replica.DiskId, replica.ReplicaIndex, localDisk, null));
                    continue;
                }

                if (!nodesById.TryGetValue(replica.NodeId, out var node)
                    || !string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                {
                    return null;
                }

                var remoteDisk = node.Disks.FirstOrDefault(disk =>
                    string.Equals(disk.DiskId, replica.DiskId, StringComparison.Ordinal)
                    && string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal));
                if (remoteDisk is null)
                {
                    return null;
                }

                targets.Add(new FullCopyWriteTarget(replica.DiskId, replica.ReplicaIndex, null, node));
            }

            return targets.Count == desiredReplicas ? targets : null;
        }
        catch (MeansException)
        {
            return null;
        }
    }

    private IReadOnlyList<FullCopyWriteTarget> PlanLocalFullCopyTargets()
    {
        return _disks
            .Where(disk => disk.Online)
            .Select(disk => new FullCopyWriteTarget(disk.DiskId, disk.SetIndex, disk, null))
            .ToArray();
    }

    private async Task<Stream> OpenErasureCodedObjectAsync(
        ObjectInfo info,
        XlObjectManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TempPath("probe.tmp"))!);
        var tempPath = TempPath(Guid.NewGuid().ToString("N") + ".ec.read.tmp");
        try
        {
            if (manifest.Parts.Count == 1)
            {
                await ReconstructErasureCodedPartAsync(info, manifest, manifest.Parts[0], tempPath, cancellationToken);
            }
            else
            {
                await ReconstructErasureCodedMultipartObjectAsync(info, manifest, tempPath, cancellationToken);
            }

            return new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                XlStreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private async Task ReconstructErasureCodedMultipartObjectAsync(
        ObjectInfo info,
        XlObjectManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            XlStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var part in manifest.Parts.OrderBy(part => part.PartNumber))
        {
            var partTempPath = TempPath(Guid.NewGuid().ToString("N") + ".ec.part.read.tmp");
            try
            {
                await ReconstructErasureCodedPartAsync(info, manifest, part, partTempPath, cancellationToken);
                await using var partStream = new FileStream(
                    partTempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    XlStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await partStream.CopyToAsync(output, cancellationToken);
            }
            finally
            {
                DeleteFileQuietly(partTempPath);
            }
        }
    }

    private async Task ReconstructErasureCodedPartAsync(
        ObjectInfo info,
        XlObjectManifest manifest,
        XlPartManifest part,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var dataShards = manifest.Erasure.DataShards;
        var shardLength = CalculateErasureShardLength(part.Size, dataShards);
        var allShards = part.Shards.ToDictionary(shard => shard.SetIndex);
        var dataShardPaths = new string?[dataShards];
        var temporaryShardPaths = new List<string>();
        var detectedLoss = false;
        try
        {
            for (var dataIndex = 0; dataIndex < dataShards; dataIndex++)
            {
                if (!allShards.TryGetValue(dataIndex, out var shard))
                {
                    detectedLoss = true;
                    continue;
                }

                var resolved = await TryResolveReadableShardPathAsync(shard, cancellationToken);
                if (resolved.DeleteOnDispose && resolved.Path is not null)
                {
                    temporaryShardPaths.Add(resolved.Path);
                }

                if (resolved.ChecksumMismatch)
                {
                    detectedLoss = true;
                    continue;
                }

                if (resolved.Path is null)
                {
                    detectedLoss = true;
                    continue;
                }

                dataShardPaths[dataIndex] = resolved.Path;
            }

            IReadOnlyList<ResolvedErasureShard> availableShards;
            byte[][]? decodeMatrix = null;
            if (dataShardPaths.All(path => path is not null))
            {
                availableShards = dataShardPaths
                    .Select((path, index) => new ResolvedErasureShard(index, path!, false))
                    .ToArray();
            }
            else
            {
                availableShards = await SelectAvailableErasureShardsAsync(manifest.Erasure, allShards, cancellationToken);
                temporaryShardPaths.AddRange(availableShards
                    .Where(shard => shard.DeleteOnDispose)
                    .Select(shard => shard.Path));
                if (availableShards.Count < dataShards)
                {
                    await QueueHealAsync(info, "ErasureReadQuorumLost", cancellationToken);
                    throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is not recoverable from EC shards.", 404);
                }

                decodeMatrix = BuildDecodeMatrix(dataShards, availableShards);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ErasureBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var remainingObjectBytes = part.Size;
            for (var dataIndex = 0; dataIndex < dataShards && remainingObjectBytes > 0; dataIndex++)
            {
                var bytesToWrite = Math.Min(shardLength, remainingObjectBytes);
                if (dataShardPaths[dataIndex] is { } path)
                {
                    await CopyShardToObjectAsync(path, output, bytesToWrite, cancellationToken);
                }
                else
                {
                    await WriteDecodedDataShardAsync(
                        output,
                        dataIndex,
                        availableShards,
                        decodeMatrix!,
                        shardLength,
                        bytesToWrite,
                        cancellationToken);
                }

                remainingObjectBytes -= bytesToWrite;
            }

            if (detectedLoss)
            {
                await QueueHealAsync(info, "ErasureShardMissingOrCorrupt", cancellationToken);
            }
        }
        finally
        {
            foreach (var path in temporaryShardPaths.Distinct(StoragePathComparer))
            {
                DeleteFileQuietly(path);
            }
        }
    }

    private async Task<IReadOnlyList<ResolvedErasureShard>> SelectAvailableErasureShardsAsync(
        XlErasureInfo erasure,
        IReadOnlyDictionary<int, XlShardManifest> allShards,
        CancellationToken cancellationToken)
    {
        var available = new List<ResolvedErasureShard>(erasure.DataShards);
        for (var shardIndex = 0; shardIndex < erasure.DataShards + erasure.ParityShards; shardIndex++)
        {
            if (available.Count == erasure.DataShards)
            {
                break;
            }

            if (!allShards.TryGetValue(shardIndex, out var shard))
            {
                continue;
            }

            var resolved = await TryResolveReadableShardPathAsync(shard, cancellationToken);
            if (resolved.Path is null || resolved.ChecksumMismatch)
            {
                continue;
            }

            available.Add(new ResolvedErasureShard(shardIndex, resolved.Path, resolved.DeleteOnDispose));
        }

        return available;
    }

    private static async Task CopyShardToObjectAsync(
        string sourcePath,
        Stream output,
        long bytesToWrite,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ErasureBufferSize);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, ErasureBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var remaining = bytesToWrite;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read <= 0)
                {
                    throw new EndOfStreamException("EC data shard ended before the object was reconstructed.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteDecodedDataShardAsync(
        Stream output,
        int dataIndex,
        IReadOnlyList<ResolvedErasureShard> availableShards,
        byte[][] decodeMatrix,
        long shardLength,
        long bytesToWrite,
        CancellationToken cancellationToken)
    {
        var outputBuffer = ArrayPool<byte>.Shared.Rent(ErasureBufferSize);
        var readBuffers = availableShards.Select(_ => ArrayPool<byte>.Shared.Rent(ErasureBufferSize)).ToArray();
        var streams = new List<FileStream>(availableShards.Count);
        try
        {
            foreach (var shard in availableShards)
            {
                streams.Add(new FileStream(shard.Path, FileMode.Open, FileAccess.Read, FileShare.Read, ErasureBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            var remainingShard = shardLength;
            var remainingWrite = bytesToWrite;
            while (remainingShard > 0)
            {
                var toProcess = (int)Math.Min(outputBuffer.Length, remainingShard);
                Array.Clear(outputBuffer, 0, toProcess);
                for (var shardIndex = 0; shardIndex < streams.Count; shardIndex++)
                {
                    await streams[shardIndex].ReadExactlyAsync(readBuffers[shardIndex].AsMemory(0, toProcess), cancellationToken);
                    XorMultiply(outputBuffer, readBuffers[shardIndex], decodeMatrix[dataIndex][shardIndex], toProcess);
                }

                if (remainingWrite > 0)
                {
                    var toWrite = (int)Math.Min(toProcess, remainingWrite);
                    await output.WriteAsync(outputBuffer.AsMemory(0, toWrite), cancellationToken);
                    remainingWrite -= toWrite;
                }

                remainingShard -= toProcess;
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }

            ArrayPool<byte>.Shared.Return(outputBuffer);
            foreach (var readBuffer in readBuffers)
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }
        }
    }

    private async Task WriteManifestCopiesAsync(
        string bucketName,
        string objectId,
        XlObjectManifest manifest,
        IReadOnlyList<XlShardManifest> shards,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, XlJsonContext.Default.XlObjectManifest);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var checksum = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        var manifestTargets = shards
            .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(shard => shard.SetIndex).First())
            .ToList();
        if (manifestTargets.All(shard => _disks.All(disk => !string.Equals(disk.DiskId, shard.DiskId, StringComparison.Ordinal)))
            && _disks.FirstOrDefault(disk => disk.Online) is { } localDisk)
        {
            manifestTargets.Add(new XlShardManifest(
                localDisk.DiskId,
                localDisk.SetIndex,
                ObjectManifestRelativePath(bucketName, objectId),
                jsonBytes.Length,
                checksum));
        }

        var writtenCopies = new ConcurrentBag<XlShardManifest>();
        await Parallel.ForEachAsync(
            manifestTargets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ShardTransferMaxConcurrency
            },
            async (shard, token) =>
            {
                try
                {
                    var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                    if (disk is not null)
                    {
                        var manifestPath = Path.Combine(disk.RootPath, ObjectManifestRelativePath(bucketName, objectId));
                        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                        await File.WriteAllBytesAsync(manifestPath, jsonBytes, token);
                        writtenCopies.Add(shard);
                        return;
                    }

                    if (!_shardTransport.Enabled)
                    {
                        throw new MeansException(MeansErrorCodes.InvalidRequest, "Cluster shard transport is not configured for remote manifest replication.", 503);
                    }

                    var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, token)
                        ?? throw new MeansException(MeansErrorCodes.SlowDown, "Remote manifest target is not online.", 503);
                    using var content = new MemoryStream(jsonBytes);
                    await _shardTransport.WriteManifestAsync(
                        node,
                        shard.DiskId,
                        ObjectManifestRelativePath(bucketName, objectId),
                        content,
                        jsonBytes.Length,
                        checksum,
                        token);
                    writtenCopies.Add(shard);
                }
                catch when (!token.IsCancellationRequested)
                {
                }
            });

        var manifestWriteQuorum = Math.Min(manifestTargets.Count, Math.Max(1, manifest.Erasure.WriteQuorum));
        if (writtenCopies.Count < manifestWriteQuorum)
        {
            throw new MeansException(MeansErrorCodes.SlowDown, "Object manifest write quorum was not met.", 503);
        }
    }

    private static bool IsReedSolomonErasure(XlObjectManifest manifest)
    {
        return string.Equals(manifest.Erasure.Algorithm, ReedSolomonAlgorithm, StringComparison.Ordinal);
    }

    private int ErasureCellSizeBytes => 128 * 1024;

    private static int ErasureBufferSize => 1024 * 1024;

    private static int ErasureWriteQuorum(int dataShards, int parityShards)
    {
        return parityShards == dataShards ? dataShards + 1 : dataShards;
    }

    private static bool CanRecoverErasureData(int dataShards, IEnumerable<int> shardIndexes)
    {
        var available = shardIndexes
            .Distinct()
            .OrderBy(index => index)
            .Take(dataShards)
            .Select(index => new ResolvedErasureShard(index, string.Empty, false))
            .ToArray();
        if (available.Length < dataShards)
        {
            return false;
        }

        try
        {
            _ = BuildDecodeMatrix(dataShards, available);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPreparedShardCommitted(PreparedShard shard)
    {
        return shard.Target.LocalDisk is not null || shard.RemoteUploaded;
    }

    private static long CalculateErasureShardLength(long contentLength, int dataShards)
    {
        return contentLength == 0 ? 0 : (contentLength + dataShards - 1) / dataShards;
    }

    private static string ErasureDiskScore(string bucketName, string objectId, XlDisk disk)
    {
        var input = string.Join('|', bucketName, objectId, disk.DiskId, disk.SetIndex);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string ObjectShardRelativePath(string bucketName, string objectId, int shardIndex)
    {
        return Path.Combine("objects", BucketHash(bucketName), objectId, "shard." + shardIndex.ToString("D2"));
    }

    private string RemoteShardStagingPath(string objectId, int shardIndex)
    {
        return TempPath(objectId + ".shard." + shardIndex.ToString("D2") + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private void DeleteShardFilesQuietly(IReadOnlyList<XlShardManifest> shards)
    {
        foreach (var shard in shards)
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is not null)
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
            }
        }
    }

    private async Task DeleteShardCopiesQuietlyAsync(
        IReadOnlyList<XlShardManifest> shards,
        CancellationToken cancellationToken)
    {
        foreach (var shard in shards)
        {
            try
            {
                var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
                if (disk is not null)
                {
                    DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
                    continue;
                }

                if (!_shardTransport.Enabled)
                {
                    continue;
                }

                var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
                if (node is not null)
                {
                    await _shardTransport.DeleteShardAsync(node, shard.DiskId, shard.RelativePath, cancellationToken);
                }
            }
            catch
            {
            }
        }
    }

    private static byte[][] BuildDecodeMatrix(int dataShards, IReadOnlyList<ResolvedErasureShard> availableShards)
    {
        var matrix = new byte[dataShards][];
        for (var rowIndex = 0; rowIndex < availableShards.Count; rowIndex++)
        {
            var shard = availableShards[rowIndex];
            if (shard.ShardIndex < dataShards)
            {
                var row = new byte[dataShards];
                row[shard.ShardIndex] = 1;
                matrix[rowIndex] = row;
            }
            else
            {
                matrix[rowIndex] = BuildParityRow(dataShards, shard.ShardIndex - dataShards);
            }
        }

        return InvertMatrix(matrix);
    }

    private static byte[] BuildParityRow(int dataShards, int parityIndex)
    {
        var row = new byte[dataShards];
        for (var dataIndex = 0; dataIndex < dataShards; dataIndex++)
        {
            row[dataIndex] = ParityCoefficient(parityIndex, dataIndex);
        }

        return row;
    }

    private static byte ParityCoefficient(int parityIndex, int dataIndex)
    {
        if (dataIndex == 0)
        {
            return 1;
        }

        var value = (byte)1;
        var factor = checked((byte)(parityIndex + 1));
        for (var index = 0; index < dataIndex; index++)
        {
            value = GfMultiply(value, factor);
        }

        return value;
    }

    private static byte[][] InvertMatrix(byte[][] matrix)
    {
        var size = matrix.Length;
        var augmented = new byte[size][];
        for (var row = 0; row < size; row++)
        {
            augmented[row] = new byte[size * 2];
            Array.Copy(matrix[row], augmented[row], size);
            augmented[row][size + row] = 1;
        }

        for (var column = 0; column < size; column++)
        {
            var pivot = column;
            while (pivot < size && augmented[pivot][column] == 0)
            {
                pivot++;
            }

            if (pivot == size)
            {
                throw new MeansException(MeansErrorCodes.NoSuchKey, "EC decode matrix is not invertible.", 404);
            }

            if (pivot != column)
            {
                (augmented[pivot], augmented[column]) = (augmented[column], augmented[pivot]);
            }

            var pivotValue = augmented[column][column];
            if (pivotValue != 1)
            {
                var inverse = GfInverse(pivotValue);
                for (var item = 0; item < size * 2; item++)
                {
                    augmented[column][item] = GfMultiply(augmented[column][item], inverse);
                }
            }

            for (var row = 0; row < size; row++)
            {
                if (row == column)
                {
                    continue;
                }

                var factor = augmented[row][column];
                if (factor == 0)
                {
                    continue;
                }

                for (var item = 0; item < size * 2; item++)
                {
                    augmented[row][item] ^= GfMultiply(factor, augmented[column][item]);
                }
            }
        }

        var inverseMatrix = new byte[size][];
        for (var row = 0; row < size; row++)
        {
            inverseMatrix[row] = new byte[size];
            Array.Copy(augmented[row], size, inverseMatrix[row], 0, size);
        }

        return inverseMatrix;
    }

    private static void XorMultiply(byte[] target, byte[] source, byte coefficient, int length)
    {
        if (coefficient == 0)
        {
            return;
        }

        if (coefficient == 1)
        {
            for (var index = 0; index < length; index++)
            {
                target[index] ^= source[index];
            }

            return;
        }

        for (var index = 0; index < length; index++)
        {
            target[index] ^= GfMultiply(source[index], coefficient);
        }
    }

    private static byte GfMultiply(byte left, byte right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return GfExp[GfLog[left] + GfLog[right]];
    }

    private static byte GfInverse(byte value)
    {
        if (value == 0)
        {
            throw new DivideByZeroException("Cannot invert zero in GF(256).");
        }

        return GfExp[255 - GfLog[value]];
    }

    private static (byte[] Exp, byte[] Log) CreateGfTables()
    {
        var exp = new byte[512];
        var log = new byte[256];
        var value = 1;
        for (var index = 0; index < 255; index++)
        {
            exp[index] = (byte)value;
            log[value] = (byte)index;
            value <<= 1;
            if ((value & 0x100) != 0)
            {
                value ^= 0x11d;
            }
        }

        for (var index = 255; index < exp.Length; index++)
        {
            exp[index] = exp[index - 255];
        }

        return (exp, log);
    }

    private async Task<ShardOpenResult> TryOpenReadableShardAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        if (disk is null)
        {
            var remote = await TryDownloadRemoteShardToTempPathAsync(shard, cancellationToken);
            if (remote.Path is null)
            {
                return new ShardOpenResult(null, null, remote.ChecksumMismatch);
            }

            return new ShardOpenResult(
                new FileStream(
                    remote.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    XlStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose),
                remote.Path,
                false);
        }

        var path = Path.Combine(disk.RootPath, shard.RelativePath);
        if (!File.Exists(path))
        {
            return new ShardOpenResult(null, path, false);
        }

        if (_options.VerifyChecksumOnRead
            && !string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ShardOpenResult(null, path, true);
        }

        return new ShardOpenResult(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan),
            path,
            false);
    }

    private async Task<ShardPathResult> TryResolveReadableShardPathAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        if (disk is null)
        {
            return await TryDownloadRemoteShardToTempPathAsync(shard, cancellationToken);
        }

        var path = Path.Combine(disk.RootPath, shard.RelativePath);
        if (!File.Exists(path))
        {
            return new ShardPathResult(null, false, false);
        }

        if (_options.VerifyChecksumOnRead
            && !string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ShardPathResult(null, true, false);
        }

        return new ShardPathResult(path, false, false);
    }

    private async Task<ShardPathResult> TryDownloadRemoteShardToTempPathAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        if (!_shardTransport.Enabled)
        {
            return new ShardPathResult(null, false, false);
        }

        var node = await TryFindRemoteNodeForDiskAsync(shard.DiskId, cancellationToken);
        if (node is null)
        {
            return new ShardPathResult(null, false, false);
        }

        var tempPath = TempPath("remote-read." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using var remote = await _shardTransport.OpenShardAsync(
                node,
                shard.DiskId,
                shard.RelativePath,
                cancellationToken);
            var (length, checksum) = await CopyRemoteShardToTempPathAsync(remote.Content, tempPath, cancellationToken);
            if (length != shard.Size
                || (_options.VerifyChecksumOnRead
                    && !string.Equals(checksum, shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase)))
            {
                DeleteFileQuietly(tempPath);
                return new ShardPathResult(null, true, false);
            }

            return new ShardPathResult(tempPath, false, true);
        }
        catch (MeansException)
        {
            DeleteFileQuietly(tempPath);
            return new ShardPathResult(null, false, false);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private async Task<ClusterNodeInfo?> TryFindRemoteNodeForDiskAsync(
        string diskId,
        CancellationToken cancellationToken)
    {
        try
        {
            var topology = await GetClusterTopologyAsync(
                DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(60)),
                cancellationToken);
            return topology.Nodes
                .Where(node => string.Equals(node.Status, ClusterNodeStatuses.Online, StringComparison.Ordinal))
                .FirstOrDefault(node => node.Disks.Any(disk =>
                    string.Equals(disk.DiskId, diskId, StringComparison.Ordinal)
                    && string.Equals(disk.Status, StorageDiskStatuses.Online, StringComparison.Ordinal)));
        }
        catch (MeansException)
        {
            return null;
        }
    }

    private static async Task<(long Length, string ChecksumSha256)> CopyRemoteShardToTempPathAsync(
        Stream content,
        string tempPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(XlStreamBufferSize);
        long length = 0;
        try
        {
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha256.AppendData(buffer.AsSpan(0, read));
                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (length, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private sealed record ShardOpenResult(Stream? Stream, string? Path, bool ChecksumMismatch);

    private sealed record ShardPathResult(string? Path, bool ChecksumMismatch, bool DeleteOnDispose);

    private sealed record ShardReadSegment(string Path, long Length);

    private sealed record ShardWriteTarget(string DiskId, XlDisk? LocalDisk, ClusterNodeInfo? RemoteNode);

    private sealed record FullCopyWriteTarget(string DiskId, int SetIndex, XlDisk? LocalDisk, ClusterNodeInfo? RemoteNode);

    private sealed class PreparedShard(
        ShardWriteTarget target,
        XlShardManifest manifest,
        string localPath,
        bool deleteLocalAfterUpload)
    {
        public ShardWriteTarget Target { get; } = target;

        public XlShardManifest Manifest { get; set; } = manifest;

        public string LocalPath { get; } = localPath;

        public bool DeleteLocalAfterUpload { get; } = deleteLocalAfterUpload;

        public bool RemoteUploaded { get; set; }
    }

    private sealed record XlErasureWriteResult(XlErasureInfo Erasure, IReadOnlyList<XlShardManifest> Shards, int FailedShardCount);

    private sealed record ResolvedErasureShard(int ShardIndex, string Path, bool DeleteOnDispose);

    private sealed class XlCompositeReadStream : Stream
    {
        private readonly IReadOnlyList<ShardReadSegment> _segments;
        private readonly long _length;
        private int _segmentIndex;
        private long _segmentOffset;
        private long _position;
        private FileStream? _current;

        public XlCompositeReadStream(IReadOnlyList<ShardReadSegment> segments)
        {
            _segments = segments;
            _length = segments.Sum(segment => segment.Length);
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var totalRead = 0;
            while (count > 0 && _segmentIndex < _segments.Count)
            {
                var current = EnsureCurrentStream();
                var read = current.Read(buffer, offset, count);
                if (read == 0)
                {
                    AdvanceSegment();
                    continue;
                }

                totalRead += read;
                offset += read;
                count -= read;
                _position += read;
                _segmentOffset += read;
                if (_segmentOffset >= _segments[_segmentIndex].Length)
                {
                    AdvanceSegment();
                }

                break;
            }

            return totalRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var totalRead = 0;
            while (buffer.Length > 0 && _segmentIndex < _segments.Count)
            {
                var current = EnsureCurrentStream();
                var max = (int)Math.Min(buffer.Length, _segments[_segmentIndex].Length - _segmentOffset);
                var read = await current.ReadAsync(buffer[..max], cancellationToken);
                if (read == 0)
                {
                    AdvanceSegment();
                    continue;
                }

                totalRead += read;
                _position += read;
                _segmentOffset += read;
                if (_segmentOffset >= _segments[_segmentIndex].Length)
                {
                    AdvanceSegment();
                }

                break;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0)
            {
                throw new IOException("Cannot seek before the beginning of the stream.");
            }

            _current?.Dispose();
            _current = null;
            _position = Math.Min(target, _length);
            _segmentIndex = 0;
            _segmentOffset = 0;
            var remaining = _position;
            while (_segmentIndex < _segments.Count && remaining >= _segments[_segmentIndex].Length)
            {
                remaining -= _segments[_segmentIndex].Length;
                _segmentIndex++;
            }

            _segmentOffset = remaining;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
            }

            base.Dispose(disposing);
        }

        private FileStream EnsureCurrentStream()
        {
            if (_current is not null)
            {
                return _current;
            }

            var segment = _segments[_segmentIndex];
            _current = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (_segmentOffset > 0)
            {
                _current.Seek(_segmentOffset, SeekOrigin.Begin);
            }

            return _current;
        }

        private void AdvanceSegment()
        {
            _current?.Dispose();
            _current = null;
            _segmentIndex++;
            _segmentOffset = 0;
        }
    }
}
