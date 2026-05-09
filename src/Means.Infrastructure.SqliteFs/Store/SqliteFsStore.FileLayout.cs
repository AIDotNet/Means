namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    /// <summary>
    /// Maps an opaque object id to a sharded file path.
    /// The two-character shard keeps large buckets from creating a single oversized directory.
    /// </summary>
    private string GetObjectPath(string objectId)
    {
        return Path.Combine(_options.ObjectsPath, objectId[..2], objectId);
    }

    private string GetMultipartPartPath(string partId)
    {
        return Path.Combine(_options.ObjectsPath, "multipart", partId[..2], partId);
    }

    /// <summary>
    /// Normalizes user metadata to lower-case keys while retaining case-insensitive lookup.
    /// S3 metadata headers are case-insensitive on the wire, so storing one canonical form
    /// prevents duplicate x-amz-meta-* rows with different casing.
    /// </summary>
    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        return metadata.ToDictionary(item => item.Key.ToLowerInvariant(), item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    /// <summary>
    /// Deletes obsolete or failed blob files without letting cleanup failures change API results.
    /// SQLite metadata is the source of truth; orphan cleanup can later move to a background scrubber.
    /// </summary>
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
            // Best effort cleanup; object metadata is the source of truth.
        }
    }
}
