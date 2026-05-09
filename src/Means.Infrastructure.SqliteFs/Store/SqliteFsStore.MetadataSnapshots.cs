using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<MetadataSnapshotInfo> CreateMetadataSnapshotAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot path is required.", 400);
        }

        await EnsureInitializedAsync(cancellationToken);
        var resolvedPath = ResolvePath(snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);

        await using var source = CreateConnection();
        await source.OpenAsync(cancellationToken);
        await using (var checkpoint = source.CreateCommand())
        {
            checkpoint.CommandText = "pragma wal_checkpoint(full);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = resolvedPath,
            ForeignKeys = true
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);

        var file = new FileInfo(resolvedPath);
        return new MetadataSnapshotInfo(resolvedPath, file.Length, DateTimeOffset.UtcNow);
    }

    public async Task<MetadataSnapshotInfo> RestoreMetadataSnapshotAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot path is required.", 400);
        }

        var resolvedPath = ResolvePath(snapshotPath);
        if (!File.Exists(resolvedPath))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Snapshot file does not exist.", 404);
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = resolvedPath,
                ForeignKeys = true
            }.ToString());
            await source.OpenAsync(cancellationToken);

            await using var destination = CreateConnection();
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);

            _initialized = false;
        }
        finally
        {
            _initializationLock.Release();
        }

        await EnsureInitializedAsync(cancellationToken);
        var file = new FileInfo(resolvedPath);
        return new MetadataSnapshotInfo(resolvedPath, file.Length, DateTimeOffset.UtcNow);
    }
}
