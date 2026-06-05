using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed partial class MeansLogDb : IAsyncDisposable
{
    private const uint Magic = 0x4d4c4442; // MLDB
    private const uint BinaryMagic = 0x4d4c4432; // MLD2
    private readonly string _rootPath;
    private readonly string _walPath;
    private readonly string _syncMode;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ReaderWriterLockSlim _indexLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _orderedKeys = new(StringComparer.Ordinal);
    private FileStream? _wal;

    private MeansLogDb(string rootPath, string syncMode)
    {
        _rootPath = rootPath;
        _walPath = Path.Combine(rootPath, "current.wal");
        _syncMode = syncMode;
    }

    public static async Task<MeansLogDb> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken,
        string syncMode = XlMetaSyncModes.Always)
    {
        Directory.CreateDirectory(rootPath);
        var db = new MeansLogDb(rootPath, syncMode);
        try
        {
            await db.ReplayAsync(cancellationToken);
            db._wal = new FileStream(db._walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            db._wal.Seek(0, SeekOrigin.End);
            return db;
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await db.DisposeAsync();
            throw new IOException(
                $"Failed to open metadata WAL at '{db._walPath}'. Another Means process may already be using '{rootPath}'. Each node needs its own local storage directory.",
                ex);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _wal?.Dispose();
        _indexLock.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _indexLock.EnterReadLock();
        try
        {
            return Task.FromResult(_items.TryGetValue(key, out var value) ? value.ToArray() : null);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    public Task<T?> GetJsonAsync<T>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes;
        _indexLock.EnterReadLock();
        try
        {
            _items.TryGetValue(key, out bytes);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        return Task.FromResult(bytes is null ? default : JsonSerializer.Deserialize(bytes, XlJson.TypeInfo<T>()));
    }

    public async Task PutJsonAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await PutBatchAsync([new LogDbMutation(key, JsonSerializer.SerializeToUtf8Bytes(value, XlJson.TypeInfo<T>()), false)], cancellationToken);
    }

    public async Task PutBatchAsync(IReadOnlyList<LogDbMutation> mutations, CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var payload = EncodeMutations(mutations);
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), BinaryMagic);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), Crc32(payload));
            await _wal!.WriteAsync(header, cancellationToken);
            await _wal.WriteAsync(payload, cancellationToken);
            if (string.Equals(_syncMode, XlMetaSyncModes.Always, StringComparison.OrdinalIgnoreCase))
            {
                await _wal.FlushAsync(cancellationToken);
                _wal.Flush(flushToDisk: true);
            }
            else if (string.Equals(_syncMode, XlMetaSyncModes.Batch, StringComparison.OrdinalIgnoreCase))
            {
                await _wal.FlushAsync(cancellationToken);
            }

            _indexLock.EnterWriteLock();
            try
            {
                Apply(mutations);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public LogDbStats GetStats()
    {
        _indexLock.EnterReadLock();
        try
        {
            return new LogDbStats(
                _syncMode,
                _orderedKeys.Count,
                File.Exists(_walPath) ? new FileInfo(_walPath).Length : 0);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    public Task<IReadOnlyList<KeyValuePair<string, byte[]>>> ScanPrefixAsync(string prefix, int limit, string? afterKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundedLimit = Math.Clamp(limit, 1, 100_000);
        _indexLock.EnterReadLock();
        try
        {
            var results = new List<KeyValuePair<string, byte[]>>(Math.Min(boundedLimit, 1024));
            var startKey = afterKey is not null && string.CompareOrdinal(afterKey, prefix) > 0 ? afterKey : prefix;
            var endKey = PrefixUpperBound(prefix);
            if (string.CompareOrdinal(startKey, endKey) > 0)
            {
                return Task.FromResult<IReadOnlyList<KeyValuePair<string, byte[]>>>(results);
            }

            foreach (var key in _orderedKeys.GetViewBetween(startKey, endKey))
            {
                if (afterKey is not null && string.CompareOrdinal(key, afterKey) <= 0)
                {
                    continue;
                }

                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    break;
                }

                if (_items.TryGetValue(key, out var value))
                {
                    results.Add(new KeyValuePair<string, byte[]>(key, value));
                }

                if (results.Count >= boundedLimit)
                {
                    break;
                }
            }

            return Task.FromResult<IReadOnlyList<KeyValuePair<string, byte[]>>>(results);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    public async Task<MetadataSnapshotInfo> CreateSnapshotAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var temp = snapshotPath + ".tmp";
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                List<LogDbRecord> records;
                _indexLock.EnterReadLock();
                try
                {
                    records = _orderedKeys
                        .Select(key => new LogDbRecord(key, false, Convert.ToBase64String(_items[key])))
                        .ToList();
                }
                finally
                {
                    _indexLock.ExitReadLock();
                }

                await JsonSerializer.SerializeAsync(output, records, LogDbJsonTypeInfo<List<LogDbRecord>>(), cancellationToken);
            }

            File.Move(temp, snapshotPath, overwrite: true);
            var file = new FileInfo(snapshotPath);
            return new MetadataSnapshotInfo(snapshotPath, file.Length, DateTimeOffset.UtcNow);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RestoreSnapshotAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var input = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            var records = await JsonSerializer.DeserializeAsync(input, LogDbJsonTypeInfo<List<LogDbRecord>>(), cancellationToken) ?? [];
            _indexLock.EnterWriteLock();
            try
            {
                _items.Clear();
                _orderedKeys.Clear();
                foreach (var record in records.Where(record => !record.Delete && record.Value is not null))
                {
                    _items[record.Key] = Convert.FromBase64String(record.Value!);
                    _orderedKeys.Add(record.Key);
                }

            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            _wal?.Dispose();
            File.WriteAllBytes(_walPath, []);
            _wal = new FileStream(_walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_walPath))
        {
            return;
        }

        var validLength = 0L;
        await using var input = new FileStream(_walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = new byte[12];
            var read = await input.ReadAsync(header, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (read != header.Length)
            {
                break;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            if (magic != Magic && magic != BinaryMagic)
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            if (length <= 0 || length > 32 * 1024 * 1024)
            {
                break;
            }

            var payload = new byte[length];
            read = await input.ReadAsync(payload, cancellationToken);
            if (read != length || Crc32(payload) != expectedCrc)
            {
                break;
            }

            LogDbMutation[] mutations;
            try
            {
                mutations = magic == BinaryMagic
                    ? DecodeMutations(payload)
                    : (JsonSerializer.Deserialize(payload, LogDbJsonTypeInfo<LogDbRecord[]>()) ?? [])
                        .Select(record => new LogDbMutation(
                            record.Key,
                            record.Value is null ? null : Convert.FromBase64String(record.Value),
                            record.Delete))
                        .ToArray();
            }
            catch
            {
                break;
            }

            Apply(mutations);
            validLength = input.Position;
        }

        input.Close();
        using var truncate = new FileStream(_walPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        truncate.SetLength(validLength);
    }

    private void Apply(IReadOnlyList<LogDbMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            if (mutation.Delete)
            {
                if (_items.Remove(mutation.Key))
                {
                    RemoveOrderedKey(mutation.Key);
                }
            }
            else if (mutation.Value is not null)
            {
                if (!_items.ContainsKey(mutation.Key))
                {
                    InsertOrderedKey(mutation.Key);
                }

                _items[mutation.Key] = mutation.Value.ToArray();
            }
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private void InsertOrderedKey(string key)
    {
        _orderedKeys.Add(key);
    }

    private void RemoveOrderedKey(string key)
    {
        _orderedKeys.Remove(key);
    }

    private static string PrefixUpperBound(string prefix)
    {
        return prefix + '\uffff';
    }

    private static byte[] EncodeMutations(IReadOnlyList<LogDbMutation> mutations)
    {
        using var output = new MemoryStream();
        WriteInt32(output, mutations.Count);
        foreach (var mutation in mutations)
        {
            var keyBytes = Encoding.UTF8.GetBytes(mutation.Key);
            WriteInt32(output, keyBytes.Length);
            output.Write(keyBytes);
            output.WriteByte(mutation.Delete ? (byte)1 : (byte)0);
            if (mutation.Value is null)
            {
                WriteInt32(output, -1);
                continue;
            }

            WriteInt32(output, mutation.Value.Length);
            output.Write(mutation.Value);
        }

        return output.ToArray();
    }

    private static LogDbMutation[] DecodeMutations(byte[] payload)
    {
        var offset = 0;
        var count = ReadInt32(payload, ref offset);
        if (count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException("LogDb WAL record count is invalid.");
        }

        var mutations = new LogDbMutation[count];
        for (var i = 0; i < count; i++)
        {
            var keyLength = ReadInt32(payload, ref offset);
            if (keyLength <= 0 || keyLength > payload.Length - offset)
            {
                throw new InvalidDataException("LogDb WAL key length is invalid.");
            }

            var key = Encoding.UTF8.GetString(payload, offset, keyLength);
            offset += keyLength;
            if (offset >= payload.Length)
            {
                throw new InvalidDataException("LogDb WAL delete flag is missing.");
            }

            var delete = payload[offset++] != 0;
            var valueLength = ReadInt32(payload, ref offset);
            byte[]? value = null;
            if (valueLength >= 0)
            {
                if (valueLength > payload.Length - offset)
                {
                    throw new InvalidDataException("LogDb WAL value length is invalid.");
                }

                value = payload.AsSpan(offset, valueLength).ToArray();
                offset += valueLength;
            }

            mutations[i] = new LogDbMutation(key, value, delete);
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("LogDb WAL payload has trailing bytes.");
        }

        return mutations;
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        if (payload.Length - offset < 4)
        {
            throw new InvalidDataException("LogDb WAL integer is truncated.");
        }

        var value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static JsonTypeInfo<T> LogDbJsonTypeInfo<T>()
    {
        return LogDbJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException($"JSON metadata for {typeof(T).FullName} is not registered.");
    }

    private sealed record LogDbRecord(string Key, bool Delete, string? Value);

    [JsonSerializable(typeof(List<LogDbRecord>))]
    [JsonSerializable(typeof(LogDbRecord[]))]
    private sealed partial class LogDbJsonContext : JsonSerializerContext;
}

public sealed record LogDbMutation(string Key, byte[]? Value, bool Delete);

public sealed record LogDbStats(string SyncMode, int KeyCount, long WalBytes);
