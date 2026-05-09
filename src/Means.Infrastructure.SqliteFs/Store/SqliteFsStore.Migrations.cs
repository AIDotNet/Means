using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private static readonly string[] KnownSchemaMigrations =
    [
        "0001-base-sqlite-fs",
        "0002-multipart-upload",
        "0003-cluster-replica-placement",
        "0004-erasure-coding-control-plane",
        "0005-metadata-consistency-and-maintenance",
        "0006-s3-compatibility-controls",
        "0007-replica-checksum-scrub"
    ];

    private static async Task ApplySchemaMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // These guarded ALTERs make older local databases compatible with the current DDL.
        // New databases already have the columns because EnsureInitializedAsync creates the latest schema first.
        await EnsureColumnAsync(
            connection,
            "object_versions",
            "content_type",
            "alter table object_versions add column content_type text not null default 'application/octet-stream';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "object_versions",
            "last_modified_utc",
            "alter table object_versions add column last_modified_utc text not null default '1970-01-01T00:00:00.0000000+00:00';",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "object_versions",
            "cache_control",
            "alter table object_versions add column cache_control text null;",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "object_versions",
            "content_disposition",
            "alter table object_versions add column content_disposition text null;",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "object_replicas",
            "checksum_sha256",
            "alter table object_replicas add column checksum_sha256 text null;",
            cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                create index if not exists ix_object_versions_object_id
                    on object_versions(object_id);
                create table if not exists object_version_metadata(
                    version_id text not null,
                    name text not null,
                    value text not null,
                    primary key(version_id, name),
                    foreign key(version_id) references object_versions(version_id) on delete cascade
                );
                create table if not exists object_rebalance_tasks(
                    task_id text primary key,
                    object_id text not null,
                    shard_index integer null,
                    source_path text not null,
                    target_path text null,
                    reason text not null,
                    status text not null,
                    attempts integer not null,
                    last_error text null,
                    created_utc text not null,
                    updated_utc text not null
                );
                create index if not exists ix_object_rebalance_tasks_status
                    on object_rebalance_tasks(status, updated_utc);
                create index if not exists ix_object_ec_shards_status
                    on object_ec_shards(status, object_id);
                create table if not exists object_version_tags(
                    version_id text not null,
                    name text not null,
                    value text not null,
                    primary key(version_id, name),
                    foreign key(version_id) references object_versions(version_id) on delete cascade
                );
                create table if not exists bucket_versioning(
                    bucket_name text primary key,
                    status text not null,
                    updated_utc text not null,
                    foreign key(bucket_name) references buckets(name) on delete cascade
                );
                create table if not exists bucket_lifecycle_rules(
                    bucket_name text not null,
                    rule_id text not null,
                    status text not null,
                    prefix text not null,
                    expiration_days integer null,
                    noncurrent_version_expiration_days integer null,
                    abort_incomplete_multipart_upload_days integer null,
                    primary key(bucket_name, rule_id),
                    foreign key(bucket_name) references buckets(name) on delete cascade
                );
                create table if not exists bucket_cors(
                    bucket_name text primary key,
                    cors_xml text not null,
                    updated_utc text not null,
                    foreign key(bucket_name) references buckets(name) on delete cascade
                );
                create table if not exists bucket_notifications(
                    bucket_name text primary key,
                    notification_xml text not null,
                    updated_utc text not null,
                    foreign key(bucket_name) references buckets(name) on delete cascade
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var migrationId in KnownSchemaMigrations)
        {
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.CommandText = """
                insert into schema_migrations(migration_id, applied_utc)
                values($migrationId, $applied)
                on conflict(migration_id) do nothing;
                """;
            migrationCommand.Parameters.AddWithValue("$migrationId", migrationId);
            migrationCommand.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SchemaMigrationInfo>> ListSchemaMigrationsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select migration_id, applied_utc from schema_migrations order by migration_id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var migrations = new List<SchemaMigrationInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(new SchemaMigrationInfo(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1))));
        }

        return migrations;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"pragma table_info({tableName});";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
