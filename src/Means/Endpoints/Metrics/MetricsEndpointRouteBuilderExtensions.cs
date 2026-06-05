using System.Globalization;
using System.Text;
using Means.Configuration;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Endpoints.Metrics;

public static class MetricsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMeansMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metrics", MetricsAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> MetricsAsync(
        IConsoleStore consoleStore,
        IBackgroundTaskManager backgroundTasks,
        IOptions<ClusterOptions> clusterOptions,
        CancellationToken cancellationToken)
    {
        var configuredCluster = clusterOptions.Value;
        var diagnostics = await consoleStore.GetClusterDiagnosticsAsync(ClusterOfflineBefore(configuredCluster), cancellationToken);
        diagnostics = diagnostics with
        {
            InternalTransport = new ClusterInternalTransportDiagnostics(
                !string.IsNullOrWhiteSpace(configuredCluster.InternalAuthToken),
                Math.Max(0, configuredCluster.MaxShardTransferBytes),
                Math.Clamp(configuredCluster.ShardRpcMaxConnectionsPerNode, 1, 1024),
                Math.Clamp(configuredCluster.ShardRpcRequestTimeoutSeconds, 5, 86_400),
                Math.Clamp(configuredCluster.ShardRpcPooledConnectionLifetimeSeconds, 30, 86_400)),
            BackgroundTasks = backgroundTasks.ListTasks()
        };
        var text = PrometheusMetricsFormatter.Format(diagnostics);
        return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
    }

    private static DateTimeOffset ClusterOfflineBefore(ClusterOptions options)
    {
        return DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(5, options.OfflineAfterSeconds)));
    }
}

internal static class PrometheusMetricsFormatter
{
    public static string Format(ClusterDiagnostics diagnostics)
    {
        var builder = new StringBuilder();
        var headers = new HashSet<string>(StringComparer.Ordinal);
        WriteGauge(builder, headers, "means_storage_buckets", "Current bucket count.", diagnostics.Summary.BucketCount);
        WriteGauge(builder, headers, "means_storage_objects", "Current object count.", diagnostics.Summary.ObjectCount);
        WriteGauge(builder, headers, "means_storage_object_bytes", "Current logical object bytes.", diagnostics.Summary.TotalObjectBytes);
        WriteGauge(builder, headers, "means_cluster_capacity_bytes", "Cluster capacity by state.", "state", "total", diagnostics.Summary.TotalCapacityBytes);
        WriteGauge(builder, headers, "means_cluster_capacity_bytes", "Cluster capacity by state.", "state", "available", diagnostics.Summary.AvailableCapacityBytes);
        WriteGauge(builder, headers, "means_cluster_capacity_bytes", "Cluster capacity by state.", "state", "used", diagnostics.Summary.UsedCapacityBytes);
        WriteGauge(builder, headers, "means_cluster_nodes", "Cluster node count by status.", "status", "online", diagnostics.Summary.OnlineNodeCount);
        WriteGauge(builder, headers, "means_cluster_nodes", "Cluster node count by status.", "status", "offline", diagnostics.Summary.OfflineNodeCount);
        WriteGauge(builder, headers, "means_cluster_disks", "Cluster disk count by status.", "status", "online", diagnostics.Summary.OnlineDiskCount);
        WriteGauge(builder, headers, "means_cluster_disks", "Cluster disk count by status.", "status", "offline", diagnostics.Summary.OfflineDiskCount);
        WriteGauge(builder, headers, "means_cluster_pools", "Current storage pool count.", diagnostics.Summary.PoolCount);
        WriteGauge(builder, headers, "means_object_replica_desired", "Configured desired replica count per object.", diagnostics.ObjectReplicas.DesiredReplicaCount);
        WriteGauge(builder, headers, "means_object_replica_records", "Object replica record count by state.", "state", "total", diagnostics.ObjectReplicas.ReplicaRecordCount);
        WriteGauge(builder, headers, "means_object_replica_records", "Object replica record count by state.", "state", "committed", diagnostics.ObjectReplicas.CommittedReplicaRecordCount);
        WriteGauge(builder, headers, "means_object_replica_files", "Object replica file count by state.", "state", "existing", diagnostics.ObjectReplicas.ExistingReplicaFileCount);
        WriteGauge(builder, headers, "means_object_replica_files", "Object replica file count by state.", "state", "missing", diagnostics.ObjectReplicas.MissingReplicaFileCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "under_replicated", diagnostics.ObjectReplicas.UnderReplicatedObjectCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "without_manifest", diagnostics.ObjectReplicas.ObjectsWithoutReplicaManifestCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "degraded", diagnostics.ObjectReplicas.DegradedObjectCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "recoverable_degraded", diagnostics.ObjectReplicas.RecoverableDegradedObjectCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "unrecoverable", diagnostics.ObjectReplicas.UnrecoverableObjectCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "read_quorum_lost", diagnostics.ObjectReplicas.ReadQuorumLostObjectCount);
        WriteGauge(builder, headers, "means_object_replica_objects", "Object count by replica health state.", "state", "write_quorum_lost", diagnostics.ObjectReplicas.WriteQuorumLostObjectCount);
        WriteGauge(builder, headers, "means_replica_repair_queue", "Replica repair queue count by status.", "status", "pending", diagnostics.RepairQueue.PendingCount);
        WriteGauge(builder, headers, "means_replica_repair_queue", "Replica repair queue count by status.", "status", "completed", diagnostics.RepairQueue.CompletedCount);
        WriteGauge(builder, headers, "means_replica_repair_queue", "Replica repair queue count by status.", "status", "failed", diagnostics.RepairQueue.FailedCount);
        WriteGauge(builder, headers, "means_replica_repair_queue_failed", "Failed replica repair count by retry state.", "state", "retryable", diagnostics.RepairQueue.RetryableFailedCount);
        WriteGauge(builder, headers, "means_replica_repair_queue_failed", "Failed replica repair count by retry state.", "state", "max_attempts_reached", diagnostics.RepairQueue.MaxAttemptsReachedCount);
        WriteGauge(builder, headers, "means_metadata_pending_commits", "Pending metadata commit records.", diagnostics.Metadata.PendingCommitCount);
        WriteGauge(builder, headers, "means_metadata_orphaned_replica_records", "Replica records without a live object version.", diagnostics.Metadata.OrphanedReplicaRecordCount);
        WriteGauge(builder, headers, "means_metadata_sync_mode", "Configured metadata WAL sync mode.", "mode", diagnostics.Metadata.SyncMode.ToLowerInvariant(), 1);
        WriteGauge(builder, headers, "means_metadata_durable_writes", "Whether metadata writes are fsync-durable before visibility.", diagnostics.Metadata.DurableWriteSync ? 1 : 0);
        WriteGauge(builder, headers, "means_metadata_shared_namespace", "Whether object metadata is backed by a shared distributed namespace.", diagnostics.Metadata.SharedNamespace ? 1 : 0);
        WriteGauge(builder, headers, "means_metadata_multi_node_write_risk", "Whether multiple online nodes are present while metadata is local-only.", diagnostics.Metadata.MultiNodeWriteRisk ? 1 : 0);
        WriteGauge(builder, headers, "means_metadata_wal_bytes", "Current metadata WAL size in bytes.", diagnostics.Metadata.WalBytes);
        WriteGauge(builder, headers, "means_metadata_key_count", "Current metadata key count.", diagnostics.Metadata.KeyCount);
        WriteGauge(builder, headers, "means_erasure_coding_profiles", "Erasure coding profile count by state.", "state", "enabled", diagnostics.ErasureCoding.EnabledProfileCount);
        WriteGauge(builder, headers, "means_erasure_coding_profiles", "Erasure coding profile count by state.", "state", "disabled", diagnostics.ErasureCoding.DisabledProfileCount);
        WriteGauge(builder, headers, "means_cluster_shard_rpc_enabled", "Whether internal shard RPC is enabled.", diagnostics.InternalTransport.ShardRpcEnabled ? 1 : 0);
        WriteGauge(builder, headers, "means_cluster_shard_rpc_max_transfer_bytes", "Configured maximum internal shard transfer size.", diagnostics.InternalTransport.MaxShardTransferBytes);
        WriteGauge(builder, headers, "means_cluster_shard_rpc_max_connections_per_node", "Configured outbound shard RPC connection cap per node.", diagnostics.InternalTransport.ShardRpcMaxConnectionsPerNode);
        WriteGauge(builder, headers, "means_cluster_shard_rpc_request_timeout_seconds", "Configured outbound shard RPC request timeout.", diagnostics.InternalTransport.ShardRpcRequestTimeoutSeconds);
        WriteGauge(builder, headers, "means_cluster_shard_rpc_pooled_connection_lifetime_seconds", "Configured outbound shard RPC pooled connection lifetime.", diagnostics.InternalTransport.ShardRpcPooledConnectionLifetimeSeconds);
        WriteGauge(builder, headers, "means_capacity_admission_enabled", "Whether write capacity admission watermarks are enabled.", diagnostics.CapacityAdmission.Enabled ? 1 : 0);
        WriteGauge(builder, headers, "means_capacity_admission_min_disk_available_bytes_after_write", "Minimum disk bytes that must remain after a shard write.", diagnostics.CapacityAdmission.MinimumDiskAvailableBytesAfterWrite);
        WriteGauge(builder, headers, "means_capacity_admission_min_disk_available_percent_after_write", "Minimum disk percentage that must remain after a shard write.", diagnostics.CapacityAdmission.MinimumDiskAvailablePercentAfterWrite);
        WriteGauge(builder, headers, "means_capacity_admission_disks", "Capacity admission disk count by state.", "state", "writable", diagnostics.CapacityAdmission.WritableDiskCount);
        WriteGauge(builder, headers, "means_capacity_admission_disks", "Capacity admission disk count by state.", "state", "low_watermark", diagnostics.CapacityAdmission.LowWatermarkDiskCount);
        WriteGauge(builder, headers, "means_capacity_admission_pools", "Capacity admission pool count by state.", "state", "writable", diagnostics.CapacityAdmission.WritablePoolCount);
        WriteGauge(builder, headers, "means_capacity_admission_pools", "Capacity admission pool count by state.", "state", "low_watermark", diagnostics.CapacityAdmission.LowWatermarkPoolCount);
        WriteGauge(builder, headers, "means_capacity_admission_largest_writable_object_bytes", "Largest single shard or replica that can be placed while preserving the configured watermark.", diagnostics.CapacityAdmission.LargestWritableObjectBytes);
        WriteGauge(builder, headers, "means_placement_min_fault_domains", "Configured minimum distinct fault domains per object placement.", diagnostics.PlacementPolicy.MinimumFaultDomains);
        WriteGauge(builder, headers, "means_placement_fault_domains", "Fault domain count by state.", "state", "online", diagnostics.PlacementPolicy.OnlineFaultDomainCount);
        WriteGauge(builder, headers, "means_placement_fault_domains", "Fault domain count by state.", "state", "writable", diagnostics.PlacementPolicy.WritableFaultDomainCount);
        WriteGauge(builder, headers, "means_placement_pools", "Storage pool count by placement policy state.", "state", "meeting_fault_domain_policy", diagnostics.PlacementPolicy.PoolsMeetingFaultDomainPolicy);
        WriteGauge(builder, headers, "means_placement_pools", "Storage pool count by placement policy state.", "state", "below_fault_domain_policy", diagnostics.PlacementPolicy.PoolsBelowFaultDomainPolicy);
        foreach (var task in diagnostics.BackgroundTasks)
        {
            WriteGauge(builder, headers, "means_background_task_success_count", "Background task successful run count.", "task", task.TaskId, task.SuccessCount);
            WriteGauge(builder, headers, "means_background_task_failure_count", "Background task failed run count.", "task", task.TaskId, task.FailureCount);
            WriteGauge(builder, headers, "means_background_task_last_duration_milliseconds", "Background task last run duration in milliseconds.", "task", task.TaskId, task.LastDurationMilliseconds ?? 0);
            WriteGauge(builder, headers, "means_background_task_interval_seconds", "Background task configured interval in seconds.", "task", task.TaskId, task.IntervalSeconds);
            WriteGauge(builder, headers, "means_background_task_last_success_timestamp_seconds", "Background task last successful completion timestamp.", "task", task.TaskId, task.Status == BackgroundTaskStatuses.Succeeded && task.LastCompletedAt is not null ? task.LastCompletedAt.Value.ToUnixTimeSeconds() : 0);
            WriteGauge(
                builder,
                headers,
                "means_background_task_status",
                "Background task current status.",
                new Dictionary<string, string>
                {
                    ["task"] = task.TaskId,
                    ["status"] = task.Status.ToLowerInvariant()
                },
                1);
        }

        WriteGauge(builder, headers, "means_diagnostics_generated_timestamp_seconds", "Unix timestamp of the diagnostics snapshot.", diagnostics.GeneratedAt.ToUnixTimeSeconds());
        return builder.ToString();
    }

    private static void WriteGauge(StringBuilder builder, HashSet<string> headers, string name, string help, long value)
    {
        WriteHeader(builder, headers, name, help);
        builder
            .Append(name)
            .Append(' ')
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteGauge(StringBuilder builder, HashSet<string> headers, string name, string help, double value)
    {
        WriteHeader(builder, headers, name, help);
        builder
            .Append(name)
            .Append(' ')
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteGauge(
        StringBuilder builder,
        HashSet<string> headers,
        string name,
        string help,
        IReadOnlyDictionary<string, string> labels,
        long value)
    {
        WriteHeader(builder, headers, name, help);
        builder.Append(name);
        WriteLabels(builder, labels);
        builder
            .Append(' ')
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteGauge(
        StringBuilder builder,
        HashSet<string> headers,
        string name,
        string help,
        string labelName,
        string labelValue,
        long value)
    {
        WriteHeader(builder, headers, name, help);
        builder
            .Append(name)
            .Append('{')
            .Append(EscapeLabelName(labelName))
            .Append("=\"")
            .Append(EscapeLabelValue(labelValue))
            .Append("\"} ")
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteLabels(StringBuilder builder, IReadOnlyDictionary<string, string> labels)
    {
        builder.Append('{');
        var first = true;
        foreach (var pair in labels.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder
                .Append(EscapeLabelName(pair.Key))
                .Append("=\"")
                .Append(EscapeLabelValue(pair.Value))
                .Append('"');
            first = false;
        }

        builder.Append('}');
    }

    private static void WriteHeader(StringBuilder builder, HashSet<string> headers, string name, string help)
    {
        if (!headers.Add(name))
        {
            return;
        }

        builder
            .Append("# HELP ")
            .Append(name)
            .Append(' ')
            .AppendLine(EscapeHelp(help))
            .Append("# TYPE ")
            .Append(name)
            .AppendLine(" gauge");
    }

    private static string EscapeLabelName(string value)
    {
        return value.All(character =>
            character is >= 'a' and <= 'z'
            || character is >= 'A' and <= 'Z'
            || character is >= '0' and <= '9'
            || character == '_')
            ? value
            : "label";
    }

    private static string EscapeLabelValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapeHelp(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
