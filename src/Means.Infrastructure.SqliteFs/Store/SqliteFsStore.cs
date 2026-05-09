using Means.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Means.Infrastructure.SqliteFs;

/// <summary>
/// Single-node object store implementation backed by SQLite metadata and local blob files.
/// The class is split into partial files by operation group so the storage adapter remains
/// readable while still sharing the same transaction and file-layout helpers.
/// </summary>
public sealed partial class SqliteFsStore : IObjectStore, IAccessKeyStore, IBucketPolicyRepository, IConsoleStore, IClusterStore
{
    private readonly SqliteFsOptions _options;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteFsStore(IOptions<SqliteFsOptions> options)
    {
        _options = options.Value;
        _options.DatabasePath = ResolvePath(_options.DatabasePath);
        _options.ObjectsPath = ResolvePath(_options.ObjectsPath);
    }

    /// <summary>
    /// Creates a short-lived SQLite connection.
    /// All public operations open their own connection to avoid sharing mutable connection state
    /// across concurrent ASP.NET Core requests.
    /// </summary>
    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Cache = SqliteCacheMode.Shared,

            // SQLite disables foreign-key enforcement per connection unless it is explicitly enabled.
            // The schema relies on ON DELETE CASCADE for bucket/object cleanup, so every connection must opt in.
            ForeignKeys = true
        };

        return new SqliteConnection(builder.ToString());
    }
}
