using System.Collections.Concurrent;
using System.Text.Json;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    private const int XlStreamBufferSize = 1024 * 1024;

    private async Task<IReadOnlyList<XlShardManifest>> WriteFullCopyShardsAsync(
        string sourcePath,
        Func<XlDisk, string> relativePathFactory,
        long length,
        string checksum,
        string quorumErrorMessage,
        CancellationToken cancellationToken)
    {
        var onlineDisks = _disks.Where(disk => disk.Online).ToArray();
        if (onlineDisks.Length < WriteQuorum)
        {
            throw new MeansException(MeansErrorCodes.SlowDown, quorumErrorMessage, 503);
        }

        var shards = new ConcurrentBag<XlShardManifest>();
        await Parallel.ForEachAsync(onlineDisks, cancellationToken, async (disk, token) =>
        {
            var relative = relativePathFactory(disk);
            var target = Path.Combine(disk.RootPath, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, XlStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(output, XlStreamBufferSize, token);
                shards.Add(new XlShardManifest(disk.DiskId, disk.SetIndex, relative, length, checksum));
            }
            catch
            {
                DeleteFileQuietly(target);
            }
        });

        var committed = shards.OrderBy(shard => shard.SetIndex).ToArray();
        if (committed.Length >= WriteQuorum)
        {
            return committed;
        }

        foreach (var shard in committed)
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is not null)
            {
                DeleteFileQuietly(Path.Combine(disk.RootPath, shard.RelativePath));
            }
        }

        throw new MeansException(MeansErrorCodes.SlowDown, quorumErrorMessage, 503);
    }

    private async Task WriteManifestCopiesAsync(
        string bucketName,
        string objectId,
        XlObjectManifest manifest,
        IReadOnlyList<XlShardManifest> shards,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest);
        var manifestTargets = shards
            .GroupBy(shard => shard.DiskId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(shard => shard.SetIndex).First())
            .ToArray();
        await Parallel.ForEachAsync(manifestTargets, cancellationToken, async (shard, token) =>
        {
            var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
            if (disk is null)
            {
                return;
            }

            var manifestPath = Path.Combine(disk.RootPath, ObjectManifestRelativePath(bucketName, objectId));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, json, token);
        });
    }

    private async Task<ShardOpenResult> TryOpenReadableShardAsync(
        XlShardManifest shard,
        CancellationToken cancellationToken)
    {
        var disk = _disks.FirstOrDefault(item => item.DiskId == shard.DiskId);
        if (disk is null)
        {
            return new ShardOpenResult(null, null, false);
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
            return new ShardPathResult(null, false);
        }

        var path = Path.Combine(disk.RootPath, shard.RelativePath);
        if (!File.Exists(path))
        {
            return new ShardPathResult(null, false);
        }

        if (_options.VerifyChecksumOnRead
            && !string.Equals(await ComputeFileSha256Async(path, cancellationToken), shard.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ShardPathResult(null, true);
        }

        return new ShardPathResult(path, false);
    }

    private sealed record ShardOpenResult(Stream? Stream, string? Path, bool ChecksumMismatch);

    private sealed record ShardPathResult(string? Path, bool ChecksumMismatch);

    private sealed record ShardReadSegment(string Path, long Length);

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
