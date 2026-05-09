# Means 告警规则文档

本文档描述当前 Means 单机/分布式过渡架构可直接使用的 Prometheus 采集与告警规则。规则基于 `/metrics` 端点导出的存储、集群、副本修复和 EC profile 指标。

## 采集配置

```yaml
scrape_configs:
  - job_name: means
    metrics_path: /metrics
    static_configs:
      - targets:
          - localhost:5178
```

生产环境建议通过内网或网关限制 `/metrics` 访问来源；该端点设计为 Prometheus 直接抓取，不依赖 Console cookie。

## 核心告警规则

```yaml
groups:
  - name: means-storage
    rules:
      - alert: MeansClusterNodeOffline
        expr: means_cluster_nodes{status="offline"} > 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means storage node is offline
          description: One or more storage nodes are offline. Check node heartbeat and network reachability.

      - alert: MeansClusterDiskOffline
        expr: means_cluster_disks{status="offline"} > 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means storage disk is offline
          description: One or more registered storage disks are offline or failed health probing.

      - alert: MeansReplicaFileMissing
        expr: means_object_replica_files{state="missing"} > 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: Means object replica files are missing
          description: Replica manifests reference files that are not readable on disk. Inspect repair queue immediately.

      - alert: MeansObjectUnderReplicated
        expr: means_object_replica_objects{state="under_replicated"} > 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Means objects are under-replicated
          description: One or more objects have fewer replica records than the configured desired replica count.

      - alert: MeansObjectWithoutReplicaManifest
        expr: means_object_replica_objects{state="without_manifest"} > 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Means objects have no replica manifest
          description: Objects exist without replica records. This can happen after migration from older metadata and should be repaired.

      - alert: MeansReplicaRepairBacklog
        expr: means_replica_repair_queue{status="pending"} > 0
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Means replica repair queue has pending work
          description: Replica repair backlog has not drained within 10 minutes.

      - alert: MeansReplicaRepairFailed
        expr: means_replica_repair_queue{status="failed"} > 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means replica repair failed
          description: Replica repair workers reported failures. Check storage paths and repair service logs.

      - alert: MeansReplicaRepairMaxAttemptsReached
        expr: means_replica_repair_queue_failed{state="max_attempts_reached"} > 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: Means replica repair exhausted retries
          description: At least one repair item reached the maximum retry count and needs operator action.

      - alert: MeansBackgroundTaskFailures
        expr: means_background_task_failure_count > 0
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: Means background task is failing
          description: A background task has recorded failures. Check task status in /api/console/diagnostics and service logs.

      - alert: MeansCapacityLow
        expr: means_cluster_capacity_bytes{state="available"} / means_cluster_capacity_bytes{state="total"} < 0.15
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Means storage capacity is below 15 percent
          description: Available cluster capacity is low. Add capacity or move data before writes are impacted.

      - alert: MeansCapacityCritical
        expr: means_cluster_capacity_bytes{state="available"} / means_cluster_capacity_bytes{state="total"} < 0.05
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means storage capacity is below 5 percent
          description: Available cluster capacity is critically low and write failures are likely.
```

## 指标口径

- `means_storage_buckets`：当前 bucket 数量。
- `means_storage_objects`：当前可见对象数量，不包含未完成 multipart upload。
- `means_storage_object_bytes`：当前对象逻辑大小总和。
- `means_cluster_capacity_bytes{state}`：集群容量，`state` 为 `total`、`available`、`used`。
- `means_cluster_nodes{status}`：节点数量，`status` 为 `online`、`offline`。
- `means_cluster_disks{status}`：磁盘数量，`status` 为 `online`、`offline`。
- `means_object_replica_desired`：配置期望副本数。
- `means_object_replica_records{state}`：副本元数据记录数量。
- `means_object_replica_files{state}`：副本文件存在性统计。
- `means_object_replica_objects{state}`：对象副本健康统计。
- `means_replica_repair_queue{status}`：副本修复队列状态统计。
- `means_replica_repair_queue_failed{state}`：失败修复项的重试状态。
- `means_erasure_coding_profiles{state}`：EC profile 启用/禁用数量。
- `means_background_task_success_count{task}`：后台任务成功执行次数。
- `means_background_task_failure_count{task}`：后台任务失败次数。
- `means_background_task_last_duration_milliseconds{task}`：后台任务最近一次执行耗时。
- `means_background_task_interval_seconds{task}`：后台任务配置执行间隔。
- `means_background_task_status{task,status}`：后台任务当前状态，当前状态值为 `1`。
- `means_diagnostics_generated_timestamp_seconds`：本次诊断快照生成时间。

## 处置优先级

1. `MeansReplicaFileMissing`、`MeansReplicaRepairMaxAttemptsReached`、`MeansClusterDiskOffline`：优先确认数据文件和磁盘健康，避免副本继续退化。
2. `MeansClusterNodeOffline`：确认节点进程、网络和心跳服务。
3. `MeansCapacityCritical` / `MeansCapacityLow`：扩容或迁移数据，避免写入失败。
4. `MeansObjectUnderReplicated` / `MeansObjectWithoutReplicaManifest`：检查 repair worker 是否正常运行并确认队列是否持续下降。
