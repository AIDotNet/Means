# 部署与可观测性

本文档覆盖 Means 的本地启动、Docker 部署、实验多节点拓扑、Prometheus 指标、告警规则和生产检查清单。

## 本地运行

要求：

- .NET SDK 10
- Node.js 20+，仅修改前端或 docs-site 时需要

命令：

```bash
dotnet restore Means.slnx
dotnet build Means.slnx
dotnet run --project src/Means/Means.csproj --launch-profile http
```

默认开发地址通常为：

```text
http://localhost:5178
```

本地 S3 alias：

```text
http://localhost:5178/s3/{bucket}/{key}
```

## Docker Compose

单节点默认拓扑：

```bash
docker compose up -d --build
```

实验多节点拓扑：

```bash
docker compose -f compose.multinode.yaml up -d --build
```

实验多节点地址：

```text
http://localhost:5181  means-node1
http://localhost:5182  means-node2
http://localhost:5183  means-node3
```

注意：多节点 compose 主要验证节点/磁盘拓扑、Console 页面、diagnostics 和 background task observability。当前每个节点仍是独立 XlFs 命名空间，不要在分布式 metadata/RPC 完成前把 S3 数据面流量负载均衡到这些节点。

## Console 与数据面安全

生产环境必须：

- 替换 `Means:Console:AdminUser` 和 `Means:Console:AdminPassword`。
- 替换 `Means:Storage:DefaultAccessKey` 和 `Means:Storage:DefaultSecretKey`。
- 不在浏览器、前端配置、移动端或公开日志中暴露 SecretKey。
- 让浏览器上传使用短期 presigned URL 或受控 bucket policy。
- 限制 `/metrics` 和 Console 的公网访问面。

非 Development 环境如果仍使用默认 Console 凭证，服务会拒绝启动。

## Metrics

Means 在 `/metrics` 暴露 Prometheus 文本指标，不依赖 Console cookie。示例 scrape：

```yaml
scrape_configs:
  - job_name: means
    metrics_path: /metrics
    static_configs:
      - targets:
          - localhost:5178
```

核心指标：

| 指标 | 含义 |
| --- | --- |
| `means_storage_buckets` | 当前 bucket 数量 |
| `means_storage_objects` | 当前可见对象数量，不含未完成 multipart |
| `means_storage_object_bytes` | 当前可见对象逻辑大小 |
| `means_cluster_capacity_bytes{state}` | total/available/used 容量 |
| `means_cluster_nodes{status}` | online/offline 节点数 |
| `means_cluster_disks{status}` | online/offline 磁盘数 |
| `means_object_replica_desired` | 配置期望副本数 |
| `means_object_replica_records{state}` | 副本元数据记录状态 |
| `means_object_replica_files{state}` | 副本文件存在性 |
| `means_object_replica_objects{state}` | 对象副本健康状态 |
| `means_replica_repair_queue{status}` | repair 队列状态 |
| `means_replica_repair_queue_failed{state}` | 达到最大重试等失败状态 |
| `means_erasure_coding_profiles{state}` | EC profile 启用/禁用数量 |
| `means_background_task_success_count{task}` | 后台任务成功次数 |
| `means_background_task_failure_count{task}` | 后台任务失败次数 |
| `means_background_task_last_duration_milliseconds{task}` | 最近一次任务耗时 |
| `means_background_task_interval_seconds{task}` | 任务配置周期 |
| `means_background_task_status{task,status}` | 当前任务状态 |
| `means_diagnostics_generated_timestamp_seconds` | diagnostics 快照生成时间 |

## 核心告警

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
          description: Check node heartbeat, process health, and network reachability.

      - alert: MeansClusterDiskOffline
        expr: means_cluster_disks{status="offline"} > 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means storage disk is offline
          description: A registered disk is offline or failed health probing.

      - alert: MeansReplicaFileMissing
        expr: means_object_replica_files{state="missing"} > 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: Means object replica files are missing
          description: Replica manifests reference unreadable files. Inspect repair queue immediately.

      - alert: MeansObjectUnderReplicated
        expr: means_object_replica_objects{state="under_replicated"} > 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Means objects are under-replicated
          description: Some objects have fewer replica records than the desired replica count.

      - alert: MeansReplicaRepairBacklog
        expr: means_replica_repair_queue{status="pending"} > 0
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Means repair queue has pending work
          description: Replica repair backlog has not drained within 10 minutes.

      - alert: MeansReplicaRepairMaxAttemptsReached
        expr: means_replica_repair_queue_failed{state="max_attempts_reached"} > 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: Means replica repair exhausted retries
          description: Operator action is required for at least one repair item.

      - alert: MeansBackgroundTaskFailures
        expr: means_background_task_failure_count > 0
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: Means background task is failing
          description: Check Console diagnostics and service logs.

      - alert: MeansCapacityLow
        expr: means_cluster_capacity_bytes{state="available"} / means_cluster_capacity_bytes{state="total"} < 0.15
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Means storage capacity is below 15 percent
          description: Add capacity or move data before writes are impacted.

      - alert: MeansCapacityCritical
        expr: means_cluster_capacity_bytes{state="available"} / means_cluster_capacity_bytes{state="total"} < 0.05
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Means storage capacity is below 5 percent
          description: Write failures are likely.
```

## 处置优先级

1. 先处理数据文件缺失、磁盘离线、repair 达到最大重试。
2. 再处理节点离线、网络和 heartbeat。
3. 再处理容量不足。
4. 最后检查 under-replicated、without_manifest、后台任务积压和 lifecycle/GC 失败。

## 生产检查清单

- Console 管理员密码已替换。
- 默认 access key/secret key 已替换。
- `/metrics` 只允许 Prometheus 或内网访问。
- `Means:Storage:Disks` 指向持久化卷。
- `WriteQuorum`、`ReadQuorum` 与磁盘数匹配。
- 已确认 legacy SQLite 数据不会被误认为新 XlFs 命名空间。
- 已建立 metadata snapshot 和数据目录备份策略。
- 已启用 Prometheus scrape 和核心告警。
- 如需 tracing，已启用 `Means:Telemetry:Enabled` 并配置 OTLP endpoint。
- 已验证 SDK endpoint 指向 S3 数据面，而不是 Console 根路径。
