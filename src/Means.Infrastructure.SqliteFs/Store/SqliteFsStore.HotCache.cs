using Means.Core;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private readonly object _hotObjectCacheLock = new();
    private readonly Dictionary<string, HotObjectCacheEntry> _hotObjectCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _hotObjectCacheLru = new();
    private long _hotObjectCacheBytes;

    private bool IsHotObjectCacheEligible(ObjectInfo info)
    {
        return _options.HotObjectCacheMaxBytes > 0
            && _options.HotObjectCacheMaxObjectBytes > 0
            && info.ContentLength <= _options.HotObjectCacheMaxObjectBytes
            && info.ContentLength <= _options.HotObjectCacheMaxBytes;
    }

    private bool TryGetHotObject(string objectId, out byte[] content)
    {
        lock (_hotObjectCacheLock)
        {
            if (_hotObjectCache.TryGetValue(objectId, out var entry))
            {
                _hotObjectCacheLru.Remove(entry.Node);
                _hotObjectCacheLru.AddFirst(entry.Node);
                content = entry.Content;
                return true;
            }
        }

        content = [];
        return false;
    }

    private async Task<ObjectData> OpenCachedObjectAsync(ObjectInfo info, CancellationToken cancellationToken)
    {
        if (TryGetHotObject(info.ObjectId, out var cached))
        {
            return new ObjectData(info, new MemoryStream(cached, writable: false));
        }

        var content = await OpenObjectContentAsync(info.ObjectId, cancellationToken);
        await using var stream = content.Content;
        await using var buffer = new MemoryStream((int)info.ContentLength);
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        PutHotObject(info.ObjectId, bytes);
        return new ObjectData(info, new MemoryStream(bytes, writable: false));
    }

    private void PutHotObject(string objectId, byte[] content)
    {
        var maxBytes = Math.Max(0, _options.HotObjectCacheMaxBytes);
        if (maxBytes == 0 || content.LongLength > maxBytes)
        {
            return;
        }

        lock (_hotObjectCacheLock)
        {
            if (_hotObjectCache.TryGetValue(objectId, out var existing))
            {
                _hotObjectCacheBytes -= existing.Content.LongLength;
                _hotObjectCacheLru.Remove(existing.Node);
                _hotObjectCache.Remove(objectId);
            }

            var node = new LinkedListNode<string>(objectId);
            _hotObjectCacheLru.AddFirst(node);
            _hotObjectCache[objectId] = new HotObjectCacheEntry(content, node);
            _hotObjectCacheBytes += content.LongLength;

            while (_hotObjectCacheBytes > maxBytes && _hotObjectCacheLru.Last is not null)
            {
                var evictedId = _hotObjectCacheLru.Last.Value;
                _hotObjectCacheLru.RemoveLast();
                if (_hotObjectCache.Remove(evictedId, out var evicted))
                {
                    _hotObjectCacheBytes -= evicted.Content.LongLength;
                }
            }
        }
    }

    private sealed record HotObjectCacheEntry(byte[] Content, LinkedListNode<string> Node);
}
