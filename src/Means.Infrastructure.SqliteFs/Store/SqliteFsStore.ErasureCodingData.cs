using System.Buffers;
using System.Security.Cryptography;
using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string EcShardRoleData = "Data";
    private const string EcShardRoleParity = "Parity";
    private const string EcShardStatusCommitted = "Committed";
    private static readonly byte[] GfExp = CreateGfTables().Exp;
    private static readonly byte[] GfLog = CreateGfTables().Log;

    private async Task<ObjectEcWriteSet?> WriteObjectErasureCodingAsync(
        SqliteConnection connection,
        string bucketName,
        string key,
        string objectId,
        long contentLength,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var profile = await GetActiveErasureCodingProfileAsync(connection, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var shardLength = CalculateShardLength(contentLength, profile.DataShards);
        var placements = await PlanErasureCodingShardPlacementAsync(bucketName, key, objectId, profile, shardLength, cancellationToken);
        var createdRecords = new List<ObjectEcShardRecord>(profile.TotalShards);
        try
        {
            var dataRecords = await WriteDataShardsAsync(sourcePath, objectId, profile, placements, contentLength, shardLength, cancellationToken);
            createdRecords.AddRange(dataRecords);
            var parityRecords = await WriteParityShardsAsync(objectId, profile, placements, dataRecords, shardLength, cancellationToken);
            createdRecords.AddRange(parityRecords);
        }
        catch
        {
            DeleteEcShardFilesQuietly(createdRecords);
            throw;
        }

        return new ObjectEcWriteSet(
            new ObjectEcManifest(
                objectId,
                profile.ProfileId,
                profile.DataShards,
                profile.ParityShards,
                profile.CellSizeBytes,
                contentLength,
                shardLength,
                DateTimeOffset.UtcNow),
            createdRecords);
    }

    private static long CalculateShardLength(long contentLength, int dataShards)
    {
        return contentLength == 0 ? 0 : (contentLength + dataShards - 1) / dataShards;
    }

    private static async Task<ErasureCodingProfile?> GetActiveErasureCodingProfileAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select profile_id, data_shards, parity_shards, cell_size_bytes, enabled, created_utc, updated_utc
            from erasure_coding_profiles
            where enabled = 1
            order by profile_id
            limit 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ErasureCodingProfile(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetBoolean(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6)))
            : null;
    }

    private async Task<IReadOnlyList<ObjectEcShardPlacement>> PlanErasureCodingShardPlacementAsync(
        string bucketName,
        string key,
        string objectId,
        ErasureCodingProfile profile,
        long shardLength,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(5, _options.ReplicaOfflineAfterSeconds)));
        var topology = await GetClusterTopologyAsync(cutoff, cancellationToken);
        var candidates = topology.Nodes
            .Where(node => node.Status == ClusterNodeStatuses.Online)
            .SelectMany(node => node.Disks
                .Where(disk => disk.Status == StorageDiskStatuses.Online)
                .Where(disk => disk.AvailableBytes >= shardLength)
                .Select(disk => new ObjectEcPlacementCandidate(node.NodeId, disk.DiskId, disk.PoolId, disk.MountPath)))
            .OrderBy(candidate => ComputePlacementScore(bucketName, key, objectId, candidate))
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DiskId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates =
            [
                new ObjectEcPlacementCandidate("local-node", "local-objects", "pool-1", _options.ObjectsPath)
            ];
        }

        var placements = new List<ObjectEcShardPlacement>(profile.TotalShards);
        for (var shardIndex = 0; shardIndex < profile.TotalShards; shardIndex++)
        {
            var candidate = candidates[shardIndex % candidates.Length];
            placements.Add(new ObjectEcShardPlacement(
                shardIndex,
                shardIndex < profile.DataShards ? EcShardRoleData : EcShardRoleParity,
                candidate.NodeId,
                candidate.DiskId,
                candidate.PoolId,
                GetEcShardPath(candidate.MountPath, objectId, shardIndex)));
        }

        return placements;
    }

    private static string ComputePlacementScore(
        string bucketName,
        string key,
        string objectId,
        ObjectEcPlacementCandidate candidate)
    {
        var input = string.Join('|', bucketName, key, objectId, candidate.NodeId, candidate.DiskId);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
    }

    private async Task<IReadOnlyList<ObjectEcShardRecord>> WriteDataShardsAsync(
        string sourcePath,
        string objectId,
        ErasureCodingProfile profile,
        IReadOnlyList<ObjectEcShardPlacement> placements,
        long contentLength,
        long shardLength,
        CancellationToken cancellationToken)
    {
        var records = new List<ObjectEcShardRecord>(profile.DataShards);
        await using var source = File.OpenRead(sourcePath);
        var bufferSize = EcBufferSize(profile);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var zeroBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        Array.Clear(zeroBuffer, 0, zeroBuffer.Length);
        var totalRead = 0L;
        try
        {
            for (var shardIndex = 0; shardIndex < profile.DataShards; shardIndex++)
            {
                var placement = placements[shardIndex];
                Directory.CreateDirectory(Path.GetDirectoryName(placement.ContentPath)!);
                using var sha256 = SHA256.Create();
                await using var output = new FileStream(
                    placement.ContentPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

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
                records.Add(new ObjectEcShardRecord(
                    objectId,
                    shardIndex,
                    EcShardRoleData,
                    placement.NodeId,
                    placement.DiskId,
                    placement.PoolId,
                    placement.ContentPath,
                    EcShardStatusCommitted,
                    Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant(),
                    DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(zeroBuffer);
        }

        return records;
    }

    private async Task<IReadOnlyList<ObjectEcShardRecord>> WriteParityShardsAsync(
        string objectId,
        ErasureCodingProfile profile,
        IReadOnlyList<ObjectEcShardPlacement> placements,
        IReadOnlyList<ObjectEcShardRecord> dataRecords,
        long shardLength,
        CancellationToken cancellationToken)
    {
        var parityPlacements = placements.Skip(profile.DataShards).ToArray();
        var records = new List<ObjectEcShardRecord>(parityPlacements.Length);
        var bufferSize = EcBufferSize(profile);
        var parityBuffers = parityPlacements.Select(_ => ArrayPool<byte>.Shared.Rent(bufferSize)).ToArray();
        var readBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var dataStreams = new List<FileStream>(dataRecords.Count);
        var parityStreams = new List<FileStream>(parityPlacements.Length);
        var parityHashes = new List<SHA256>(parityPlacements.Length);
        try
        {
            foreach (var dataRecord in dataRecords)
            {
                dataStreams.Add(new FileStream(
                    dataRecord.ContentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            foreach (var placement in parityPlacements)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(placement.ContentPath)!);
                parityStreams.Add(new FileStream(
                    placement.ContentPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan));
                parityHashes.Add(SHA256.Create());
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
                    var stream = dataStreams[dataIndex];
                    await stream.ReadExactlyAsync(readBuffer.AsMemory(0, toProcess), cancellationToken);
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

            for (var parityIndex = 0; parityIndex < parityPlacements.Length; parityIndex++)
            {
                parityHashes[parityIndex].TransformFinalBlock([], 0, 0);
                var placement = parityPlacements[parityIndex];
                records.Add(new ObjectEcShardRecord(
                    objectId,
                    placement.ShardIndex,
                    EcShardRoleParity,
                    placement.NodeId,
                    placement.DiskId,
                    placement.PoolId,
                    placement.ContentPath,
                    EcShardStatusCommitted,
                    Convert.ToHexString(parityHashes[parityIndex].Hash ?? []).ToLowerInvariant(),
                    DateTimeOffset.UtcNow));
            }
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
        }

        return records;
    }

    private static int EcBufferSize(ErasureCodingProfile profile)
    {
        return Math.Clamp(profile.CellSizeBytes, 64 * 1024, 1024 * 1024);
    }

    private async Task<FileStream?> TryOpenErasureCodedObjectAsync(string objectId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(_options.ObjectsPath, "tmp"));
        var tempPath = Path.Combine(_options.ObjectsPath, "tmp", Guid.NewGuid().ToString("N") + ".ec.read.tmp");
        try
        {
            var reconstructed = await TryReconstructErasureCodedObjectAsync(objectId, tempPath, cancellationToken);
            if (!reconstructed)
            {
                DeleteFileQuietly(tempPath);
                return null;
            }

            return new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private async Task<bool> TryReconstructErasureCodedObjectAsync(
        string objectId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var manifest = await ReadObjectEcManifestAsync(connection, objectId, cancellationToken);
        if (manifest is null)
        {
            return false;
        }

        var shards = (await ReadObjectEcShardsAsync(connection, objectId, cancellationToken))
            .ToDictionary(shard => shard.ShardIndex);
        var dataShards = Enumerable.Range(0, manifest.DataShards)
            .Select(index => shards.TryGetValue(index, out var shard) && File.Exists(shard.ContentPath) ? shard : null)
            .ToArray();
        var missingDataIndexes = dataShards
            .Select((shard, index) => shard is null ? index : -1)
            .Where(index => index >= 0)
            .ToArray();
        IReadOnlyList<ObjectEcShardRecord>? availableShards = null;
        byte[][]? decodeMatrix = null;
        if (missingDataIndexes.Length > 0)
        {
            availableShards = SelectAvailableEcShards(manifest, shards);
            decodeMatrix = BuildDecodeMatrix(manifest, availableShards);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var bufferSize = Math.Clamp(manifest.CellSizeBytes, 64 * 1024, 1024 * 1024);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var remainingObjectBytes = manifest.OriginalLength;
        for (var dataIndex = 0; dataIndex < manifest.DataShards && remainingObjectBytes > 0; dataIndex++)
        {
            var bytesToWrite = Math.Min(manifest.ShardLength, remainingObjectBytes);
            if (dataShards[dataIndex] is { } shard)
            {
                await CopyShardToObjectAsync(shard.ContentPath, output, bytesToWrite, bufferSize, cancellationToken);
            }
            else
            {
                await WriteDecodedDataShardAsync(
                    output,
                    dataIndex,
                    availableShards!,
                    decodeMatrix!,
                    manifest.ShardLength,
                    bytesToWrite,
                    bufferSize,
                    cancellationToken);
            }

            remainingObjectBytes -= bytesToWrite;
        }

        return true;
    }

    public async Task<int> RebuildErasureCodedObjectsAsync(int maxItems, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var manifests = await ListEcManifestsForRepairAsync(Math.Clamp(maxItems, 1, 1000), cancellationToken);
        var rebuilt = 0;
        foreach (var manifest in manifests)
        {
            rebuilt += await RebuildMissingEcShardsAsync(manifest, cancellationToken);
        }

        return rebuilt;
    }

    private async Task<IReadOnlyList<ObjectEcManifest>> ListEcManifestsForRepairAsync(int maxItems, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, profile_id, data_shards, parity_shards, cell_size_bytes, original_length, shard_length, created_utc
            from object_ec_manifests
            order by created_utc, object_id
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", maxItems);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var manifests = new List<ObjectEcManifest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            manifests.Add(ReadEcManifest(reader));
        }

        return manifests;
    }

    private async Task<int> RebuildMissingEcShardsAsync(ObjectEcManifest manifest, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var shards = (await ReadObjectEcShardsAsync(connection, manifest.ObjectId, cancellationToken))
            .ToDictionary(shard => shard.ShardIndex);
        var missingData = Enumerable.Range(0, manifest.DataShards)
            .Where(index => !shards.TryGetValue(index, out var shard) || !File.Exists(shard.ContentPath))
            .ToArray();
        var rebuilt = 0;
        if (missingData.Length > 0)
        {
            var availableShards = SelectAvailableEcShards(manifest, shards);
            var decodeMatrix = BuildDecodeMatrix(manifest, availableShards);
            var rebuiltData = 0;
            foreach (var missingDataIndex in missingData)
            {
                if (!shards.TryGetValue(missingDataIndex, out var missingShard))
                {
                    continue;
                }

                var checksum = await RebuildDataShardFileAsync(manifest, missingDataIndex, missingShard, availableShards, decodeMatrix, cancellationToken);
                await UpdateEcShardChecksumAsync(connection, missingShard, checksum, cancellationToken);
                rebuiltData++;
            }

            if (rebuiltData == 0)
            {
                return 0;
            }

            rebuilt += rebuiltData;
        }

        shards = (await ReadObjectEcShardsAsync(connection, manifest.ObjectId, cancellationToken))
            .ToDictionary(shard => shard.ShardIndex);
        for (var shardIndex = manifest.DataShards; shardIndex < manifest.DataShards + manifest.ParityShards; shardIndex++)
        {
            if (!shards.TryGetValue(shardIndex, out var parityShard) || File.Exists(parityShard.ContentPath))
            {
                continue;
            }

            if (Enumerable.Range(0, manifest.DataShards).Any(index => !shards.TryGetValue(index, out var shard) || !File.Exists(shard.ContentPath)))
            {
                continue;
            }

            var checksum = await RebuildParityShardFileAsync(manifest, parityShard, shards, cancellationToken);
            await UpdateEcShardChecksumAsync(connection, parityShard, checksum, cancellationToken);
            rebuilt++;
        }

        return rebuilt;
    }

    private async Task<string> RebuildDataShardFileAsync(
        ObjectEcManifest manifest,
        int missingDataIndex,
        ObjectEcShardRecord missingShard,
        IReadOnlyList<ObjectEcShardRecord> availableShards,
        byte[][] decodeMatrix,
        CancellationToken cancellationToken)
    {
        var bufferSize = Math.Clamp(manifest.CellSizeBytes, 64 * 1024, 1024 * 1024);
        Directory.CreateDirectory(Path.GetDirectoryName(missingShard.ContentPath)!);
        var tempPath = missingShard.ContentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteDecodedDataShardAsync(
                    output,
                    missingDataIndex,
                    availableShards,
                    decodeMatrix,
                    manifest.ShardLength,
                    manifest.ShardLength,
                    bufferSize,
                    cancellationToken);
            }

            File.Move(tempPath, missingShard.ContentPath, overwrite: true);
            return await ComputeFileSha256Async(missingShard.ContentPath, bufferSize, cancellationToken);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private async Task<string> RebuildParityShardFileAsync(
        ObjectEcManifest manifest,
        ObjectEcShardRecord parityShard,
        IReadOnlyDictionary<int, ObjectEcShardRecord> shards,
        CancellationToken cancellationToken)
    {
        var bufferSize = Math.Clamp(manifest.CellSizeBytes, 64 * 1024, 1024 * 1024);
        Directory.CreateDirectory(Path.GetDirectoryName(parityShard.ContentPath)!);
        var tempPath = parityShard.ContentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var parityBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var readBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var dataStreams = new List<FileStream>(manifest.DataShards);
        try
        {
            foreach (var shardIndex in Enumerable.Range(0, manifest.DataShards))
            {
                dataStreams.Add(new FileStream(shards[shardIndex].ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var remaining = manifest.ShardLength;
                var parityIndex = parityShard.ShardIndex - manifest.DataShards;
                while (remaining > 0)
                {
                    var toProcess = (int)Math.Min(parityBuffer.Length, remaining);
                    Array.Clear(parityBuffer, 0, toProcess);
                    for (var dataIndex = 0; dataIndex < dataStreams.Count; dataIndex++)
                    {
                        var stream = dataStreams[dataIndex];
                        await stream.ReadExactlyAsync(readBuffer.AsMemory(0, toProcess), cancellationToken);
                        XorMultiply(parityBuffer, readBuffer, ParityCoefficient(parityIndex, dataIndex), toProcess);
                    }

                    await output.WriteAsync(parityBuffer.AsMemory(0, toProcess), cancellationToken);
                    remaining -= toProcess;
                }
            }

            File.Move(tempPath, parityShard.ContentPath, overwrite: true);
            return await ComputeFileSha256Async(parityShard.ContentPath, bufferSize, cancellationToken);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
        finally
        {
            foreach (var stream in dataStreams)
            {
                await stream.DisposeAsync();
            }

            ArrayPool<byte>.Shared.Return(parityBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static async Task CopyShardToObjectAsync(
        string sourcePath,
        Stream output,
        long bytesToWrite,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var remaining = bytesToWrite;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
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
        IReadOnlyList<ObjectEcShardRecord> availableShards,
        byte[][] decodeMatrix,
        long shardLength,
        long bytesToWrite,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var outputBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var readBuffers = availableShards.Select(_ => ArrayPool<byte>.Shared.Rent(bufferSize)).ToArray();
        var dataStreams = new List<FileStream>(availableShards.Count);
        try
        {
            foreach (var shard in availableShards)
            {
                dataStreams.Add(new FileStream(shard.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }

            var remainingShard = shardLength;
            var remainingWrite = bytesToWrite;
            while (remainingShard > 0)
            {
                var toProcess = (int)Math.Min(outputBuffer.Length, remainingShard);
                Array.Clear(outputBuffer, 0, toProcess);
                for (var shardIndex = 0; shardIndex < dataStreams.Count; shardIndex++)
                {
                    await dataStreams[shardIndex].ReadExactlyAsync(readBuffers[shardIndex].AsMemory(0, toProcess), cancellationToken);
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
            foreach (var stream in dataStreams)
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

    private static async Task<string> ComputeFileSha256Async(string path, int bufferSize, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyList<ObjectEcShardRecord> SelectAvailableEcShards(
        ObjectEcManifest manifest,
        IReadOnlyDictionary<int, ObjectEcShardRecord> shards)
    {
        var available = new List<ObjectEcShardRecord>(manifest.DataShards);
        for (var shardIndex = 0; shardIndex < manifest.DataShards; shardIndex++)
        {
            if (shards.TryGetValue(shardIndex, out var shard) && File.Exists(shard.ContentPath))
            {
                available.Add(shard);
            }
        }

        for (var shardIndex = manifest.DataShards; shardIndex < manifest.DataShards + manifest.ParityShards; shardIndex++)
        {
            if (available.Count == manifest.DataShards)
            {
                break;
            }

            if (shards.TryGetValue(shardIndex, out var shard) && File.Exists(shard.ContentPath))
            {
                available.Add(shard);
            }
        }

        if (available.Count < manifest.DataShards)
        {
            throw new MeansException(MeansErrorCodes.NoSuchKey, "Object content is not recoverable from EC shards.", 404);
        }

        return available;
    }

    private static byte[][] BuildDecodeMatrix(ObjectEcManifest manifest, IReadOnlyList<ObjectEcShardRecord> availableShards)
    {
        var matrix = new byte[manifest.DataShards][];
        for (var rowIndex = 0; rowIndex < availableShards.Count; rowIndex++)
        {
            var shard = availableShards[rowIndex];
            if (string.Equals(shard.Role, EcShardRoleData, StringComparison.Ordinal))
            {
                var row = new byte[manifest.DataShards];
                row[shard.ShardIndex] = 1;
                matrix[rowIndex] = row;
            }
            else
            {
                matrix[rowIndex] = BuildParityRow(manifest.DataShards, shard.ShardIndex - manifest.DataShards);
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

    private static async Task ReplaceObjectErasureCodingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObjectEcWriteSet? writeSet,
        CancellationToken cancellationToken)
    {
        if (writeSet is null)
        {
            return;
        }

        await DeleteObjectErasureCodingRowsAsync(connection, transaction, writeSet.Manifest.ObjectId, cancellationToken);
        await using (var manifestCommand = connection.CreateCommand())
        {
            manifestCommand.Transaction = transaction;
            manifestCommand.CommandText = """
                insert into object_ec_manifests(
                    object_id,
                    profile_id,
                    data_shards,
                    parity_shards,
                    cell_size_bytes,
                    original_length,
                    shard_length,
                    created_utc)
                values(
                    $objectId,
                    $profileId,
                    $dataShards,
                    $parityShards,
                    $cellSize,
                    $originalLength,
                    $shardLength,
                    $created);
                """;
            manifestCommand.Parameters.AddWithValue("$objectId", writeSet.Manifest.ObjectId);
            manifestCommand.Parameters.AddWithValue("$profileId", writeSet.Manifest.ProfileId);
            manifestCommand.Parameters.AddWithValue("$dataShards", writeSet.Manifest.DataShards);
            manifestCommand.Parameters.AddWithValue("$parityShards", writeSet.Manifest.ParityShards);
            manifestCommand.Parameters.AddWithValue("$cellSize", writeSet.Manifest.CellSizeBytes);
            manifestCommand.Parameters.AddWithValue("$originalLength", writeSet.Manifest.OriginalLength);
            manifestCommand.Parameters.AddWithValue("$shardLength", writeSet.Manifest.ShardLength);
            manifestCommand.Parameters.AddWithValue("$created", writeSet.Manifest.CreatedAt.ToString("O"));
            await manifestCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var shard in writeSet.Shards)
        {
            await InsertObjectEcShardAsync(connection, transaction, shard, cancellationToken);
        }
    }

    private static async Task InsertObjectEcShardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObjectEcShardRecord shard,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into object_ec_shards(
                object_id,
                shard_index,
                role,
                node_id,
                disk_id,
                pool_id,
                content_path,
                status,
                checksum_sha256,
                created_utc)
            values(
                $objectId,
                $shardIndex,
                $role,
                $nodeId,
                $diskId,
                $poolId,
                $contentPath,
                $status,
                $checksum,
                $created);
            """;
        command.Parameters.AddWithValue("$objectId", shard.ObjectId);
        command.Parameters.AddWithValue("$shardIndex", shard.ShardIndex);
        command.Parameters.AddWithValue("$role", shard.Role);
        command.Parameters.AddWithValue("$nodeId", shard.NodeId);
        command.Parameters.AddWithValue("$diskId", shard.DiskId);
        command.Parameters.AddWithValue("$poolId", shard.PoolId);
        command.Parameters.AddWithValue("$contentPath", shard.ContentPath);
        command.Parameters.AddWithValue("$status", shard.Status);
        command.Parameters.AddWithValue("$checksum", shard.ChecksumSha256);
        command.Parameters.AddWithValue("$created", shard.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteObjectErasureCodingRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from object_ec_manifests where object_id = $objectId;";
        command.Parameters.AddWithValue("$objectId", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> GetObjectEcShardPathsAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        var shards = await ReadObjectEcShardsAsync(connection, objectId, cancellationToken);
        return shards.Select(shard => shard.ContentPath).ToArray();
    }

    private static async Task<ObjectEcManifest?> ReadObjectEcManifestAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, profile_id, data_shards, parity_shards, cell_size_bytes, original_length, shard_length, created_utc
            from object_ec_manifests
            where object_id = $objectId;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEcManifest(reader) : null;
    }

    private static ObjectEcManifest ReadEcManifest(SqliteDataReader reader)
    {
        return new ObjectEcManifest(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            DateTimeOffset.Parse(reader.GetString(7)));
    }

    private static async Task<IReadOnlyList<ObjectEcShardRecord>> ReadObjectEcShardsAsync(
        SqliteConnection connection,
        string objectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, shard_index, role, node_id, disk_id, pool_id, content_path, status, checksum_sha256, created_utc
            from object_ec_shards
            where object_id = $objectId
            order by shard_index;
            """;
        command.Parameters.AddWithValue("$objectId", objectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shards = new List<ObjectEcShardRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shards.Add(new ObjectEcShardRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9))));
        }

        return shards;
    }

    private static async Task UpdateEcShardChecksumAsync(
        SqliteConnection connection,
        ObjectEcShardRecord shard,
        string checksumSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update object_ec_shards
            set status = $status,
                checksum_sha256 = $checksum
            where object_id = $objectId and shard_index = $shardIndex;
            """;
        command.Parameters.AddWithValue("$status", EcShardStatusCommitted);
        command.Parameters.AddWithValue("$checksum", checksumSha256);
        command.Parameters.AddWithValue("$objectId", shard.ObjectId);
        command.Parameters.AddWithValue("$shardIndex", shard.ShardIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void DeleteEcShardFilesQuietly(IReadOnlyList<ObjectEcShardRecord> shards)
    {
        foreach (var shard in shards)
        {
            DeleteFileQuietly(shard.ContentPath);
        }
    }

    private static void DeleteEcShardFilesQuietly(IReadOnlyList<string> shardPaths)
    {
        foreach (var shardPath in shardPaths)
        {
            DeleteFileQuietly(shardPath);
        }
    }

    private static string GetEcShardPath(string mountPath, string objectId, int shardIndex)
    {
        return Path.Combine(mountPath, "ec", objectId[..2], objectId + "." + shardIndex.ToString("D2") + ".ec");
    }

    private sealed record ObjectEcWriteSet(ObjectEcManifest Manifest, IReadOnlyList<ObjectEcShardRecord> Shards);

    private sealed record ObjectEcManifest(
        string ObjectId,
        string ProfileId,
        int DataShards,
        int ParityShards,
        int CellSizeBytes,
        long OriginalLength,
        long ShardLength,
        DateTimeOffset CreatedAt);

    private sealed record ObjectEcShardRecord(
        string ObjectId,
        int ShardIndex,
        string Role,
        string NodeId,
        string DiskId,
        string PoolId,
        string ContentPath,
        string Status,
        string ChecksumSha256,
        DateTimeOffset CreatedAt);

    private sealed record ObjectEcShardPlacement(
        int ShardIndex,
        string Role,
        string NodeId,
        string DiskId,
        string PoolId,
        string ContentPath);

    private sealed record ObjectEcPlacementCandidate(string NodeId, string DiskId, string PoolId, string MountPath);
}
