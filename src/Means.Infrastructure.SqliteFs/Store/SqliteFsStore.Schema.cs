namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    /// <summary>
    /// Lazily creates the local database schema and object directory.
    /// Initialization is guarded because the first wave of HTTP requests can arrive concurrently
    /// after process start; only one request should issue DDL and seed the default access key.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            Directory.CreateDirectory(_options.ObjectsPath);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                // WAL keeps readers and writers from blocking each other in the common path.
                // The schema stores object metadata separately from blob bytes so a future
                // distributed backend can replace the file layout without changing the data-plane API.
                command.CommandText = """
                    pragma journal_mode = wal;
                    create table if not exists buckets(
                        name text primary key,
                        created_utc text not null
                    );
                    create table if not exists objects(
                        bucket_name text not null,
                        key text not null,
                        object_id text not null,
                        etag text not null,
                        content_length integer not null,
                        content_type text not null,
                        last_modified_utc text not null,
                        cache_control text null,
                        content_disposition text null,
                        primary key(bucket_name, key),
                        foreign key(bucket_name) references buckets(name) on delete cascade
                    );
                    create table if not exists object_metadata(
                        bucket_name text not null,
                        key text not null,
                        name text not null,
                        value text not null,
                        primary key(bucket_name, key, name),
                        foreign key(bucket_name, key) references objects(bucket_name, key) on delete cascade
                    );
                    create table if not exists multipart_uploads(
                        upload_id text primary key,
                        bucket_name text not null,
                        key text not null,
                        content_type text not null,
                        cache_control text null,
                        content_disposition text null,
                        initiated_utc text not null,
                        foreign key(bucket_name) references buckets(name) on delete cascade
                    );
                    create index if not exists ix_multipart_uploads_bucket_key
                        on multipart_uploads(bucket_name, key, upload_id);
                    create table if not exists multipart_upload_metadata(
                        upload_id text not null,
                        name text not null,
                        value text not null,
                        primary key(upload_id, name),
                        foreign key(upload_id) references multipart_uploads(upload_id) on delete cascade
                    );
                    create table if not exists multipart_parts(
                        upload_id text not null,
                        part_number integer not null,
                        part_id text not null,
                        etag text not null,
                        content_length integer not null,
                        last_modified_utc text not null,
                        primary key(upload_id, part_number),
                        foreign key(upload_id) references multipart_uploads(upload_id) on delete cascade
                    );
                    create table if not exists bucket_policies(
                        bucket_name text primary key,
                        policy_json text not null,
                        updated_utc text not null,
                        foreign key(bucket_name) references buckets(name) on delete cascade
                    );
                    create table if not exists bucket_settings(
                        bucket_name text primary key,
                        default_response_headers_json text not null,
                        default_metadata_json text not null,
                        updated_utc text not null,
                        foreign key(bucket_name) references buckets(name) on delete cascade
                    );
                    create table if not exists access_keys(
                        access_key text primary key,
                        secret_key text not null,
                        enabled integer not null,
                        created_utc text not null
                    );
                    create table if not exists audit_entries(
                        id integer primary key autoincrement,
                        occurred_utc text not null,
                        actor text not null,
                        action text not null,
                        resource text not null,
                        status text not null,
                        message text null
                    );
                    create table if not exists request_hourly_metrics(
                        hour_utc text not null,
                        bucket_name text not null,
                        request_count integer not null,
                        error_count integer not null,
                        ingress_bytes integer not null,
                        egress_bytes integer not null,
                        put_count integer not null,
                        get_count integer not null,
                        delete_count integer not null,
                        head_count integer not null,
                        list_count integer not null,
                        last_activity_utc text not null,
                        primary key(hour_utc, bucket_name)
                    );
                    create index if not exists ix_request_hourly_metrics_activity
                        on request_hourly_metrics(last_activity_utc desc);
                    create table if not exists system_settings(
                        key text primary key,
                        value text not null,
                        updated_utc text not null
                    );
                    create table if not exists storage_clusters(
                        cluster_id text primary key,
                        name text not null,
                        created_utc text not null
                    );
                    create table if not exists storage_pools(
                        pool_id text primary key,
                        cluster_id text not null,
                        name text not null,
                        created_utc text not null,
                        foreign key(cluster_id) references storage_clusters(cluster_id) on delete cascade
                    );
                    create table if not exists storage_nodes(
                        node_id text primary key,
                        cluster_id text not null,
                        host_name text not null,
                        endpoint text not null,
                        status text not null,
                        registered_utc text not null,
                        last_heartbeat_utc text not null,
                        foreign key(cluster_id) references storage_clusters(cluster_id) on delete cascade
                    );
                    create index if not exists ix_storage_nodes_cluster_status
                        on storage_nodes(cluster_id, status, last_heartbeat_utc);
                    create table if not exists storage_disks(
                        node_id text not null,
                        disk_id text not null,
                        pool_id text not null,
                        mount_path text not null,
                        total_bytes integer not null,
                        available_bytes integer not null,
                        status text not null,
                        last_seen_utc text not null,
                        primary key(node_id, disk_id),
                        foreign key(node_id) references storage_nodes(node_id) on delete cascade,
                        foreign key(pool_id) references storage_pools(pool_id) on delete cascade
                    );
                    create index if not exists ix_storage_disks_pool
                        on storage_disks(pool_id, status);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var seed = connection.CreateCommand())
            {
                // The seed credential gives local development a deterministic key pair.
                // "do nothing" keeps operator-managed credentials stable across restarts.
                seed.CommandText = """
                    insert into access_keys(access_key, secret_key, enabled, created_utc)
                    values($accessKey, $secretKey, 1, $created)
                    on conflict(access_key) do nothing;
                    """;
                seed.Parameters.AddWithValue("$accessKey", _options.DefaultAccessKey);
                seed.Parameters.AddWithValue("$secretKey", _options.DefaultSecretKey);
                seed.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await seed.ExecuteNonQueryAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
