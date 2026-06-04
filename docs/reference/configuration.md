# 配置参考

Means 使用标准 ASP.NET Core configuration。默认配置位于 `src/Means/appsettings.json`，开发环境覆盖位于 `src/Means/appsettings.Development.json`。生产部署应通过环境变量、容器 secret 或配置中心覆盖敏感字段。

## S3 地址配置

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Means:S3:ServiceHost` | `api.means.local` | path-style S3 host |
| `Means:S3:DomainSuffix` | `means.local` | virtual-hosted-style bucket suffix |
| `Means:S3:AliasPrefix` | `/s3` | 同源 S3 alias 路径，代码默认值来自 `S3AddressingOptions` |

SDK endpoint 应指向数据面。例如同源 alias 部署时使用 `https://host/s3/`；独立 S3 host 部署时使用 `https://api.means.local/`。

## 存储配置

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Means:Storage:Backend` | `XlFs` | 默认存储后端；非 XlFs 时使用 SqliteFs legacy/test adapter |
| `Means:Storage:DatabasePath` | `/data/means.db` | SqliteFs metadata 路径；XlFs 用于 legacy 检测 |
| `Means:Storage:ObjectsPath` | `/data/objects` | 未配置多盘时的对象根目录 |
| `Means:Storage:Disks` | `/data/xlfs/disk1..4` | XlFs 多盘根目录 |
| `Means:Storage:DeploymentId` | 空 | XlFs deployment 标识，空时生成或从格式读取 |
| `Means:Storage:SetId` | `set-1` | XlFs set 标识 |
| `Means:Storage:WriteQuorum` | `3` | 写成功 quorum |
| `Means:Storage:ReadQuorum` | `1` | 读 quorum |
| `Means:Storage:MetaSyncMode` | `Always` | MeansLogDb WAL flush 策略 |
| `Means:Storage:AllowNewFormatWithExistingSqlite` | `false` | 检测到旧 SQLite 文件且没有 XlFs format 时是否允许创建新命名空间 |
| `Means:Storage:ReplicaCount` | `1` | 期望副本数 |
| `Means:Storage:VerifyChecksumOnRead` | `false` | GET 读路径是否同步校验 SHA-256 |
| `Means:Storage:HotObjectCacheMaxBytes` | `67108864` | 热点小对象缓存总预算 |
| `Means:Storage:HotObjectCacheMaxObjectBytes` | `1048576` | 单个可缓存对象上限 |
| `Means:Storage:DefaultAccessKey` | `meansadmin` | 初始 S3 access key |
| `Means:Storage:DefaultSecretKey` | `meansadminsecret` | 初始 S3 secret key |

## 维护任务配置

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `ReplicaOfflineAfterSeconds` | `60` | 副本离线判定窗口 |
| `ReplicaRepairIntervalSeconds` | `300` | 副本 repair 周期 |
| `ReplicaRepairBatchSize` | `100` | 每批 repair 数量 |
| `ReplicaRepairMaxAttempts` | `5` | repair 最大重试 |
| `RebalanceIntervalSeconds` | `600` | rebalance 周期 |
| `RebalanceBatchSize` | `100` | rebalance 批量 |
| `ErasureCodingRepairIntervalSeconds` | `600` | EC repair 周期 |
| `LifecycleIntervalSeconds` | `3600` | lifecycle worker 周期 |
| `LifecycleBatchSize` | `100` | lifecycle 批量 |
| `ScrubIntervalSeconds` | `3600` | scrub 周期 |
| `ScrubBatchSize` | `100` | scrub 批量 |
| `MetadataConsistencyIntervalSeconds` | `3600` | metadata consistency 周期 |
| `MetadataConsistencyBatchSize` | `1000` | consistency 检查批量 |
| `GarbageCollectionIntervalSeconds` | `3600` | storage GC 周期 |
| `GarbageCollectionBatchSize` | `1000` | GC 批量 |
| `GarbageCollectionTempFileAgeMinutes` | `60` | temp/orphan 文件年龄保护 |
| `MultipartUploadCleanupAgeHours` | `24` | 未完成 multipart 清理年龄 |
| `MultipartUploadCleanupIntervalMinutes` | `60` | multipart cleanup 周期 |
| `ReplicationIntervalSeconds` | `3600` | replication worker 预留周期 |

## Cluster 配置

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Means:Cluster:ClusterId` | `local` | 集群 ID |
| `Means:Cluster:ClusterName` | `Local Means Cluster` | 集群显示名 |
| `Means:Cluster:NodeId` | 空 | 节点 ID，空时服务生成 |
| `Means:Cluster:NodeEndpoint` | `http://localhost` | 节点对外端点 |
| `Means:Cluster:PoolId` | `pool-1` | 存储池 ID |
| `Means:Cluster:PoolName` | `Pool 1` | 存储池显示名 |
| `Means:Cluster:ObjectDiskId` | `local-objects` | 单对象盘 ID |
| `Means:Cluster:PlacementSeed` | `means-v1` | deterministic placement seed |
| `Means:Cluster:HeartbeatIntervalSeconds` | `15` | heartbeat 周期 |
| `Means:Cluster:OfflineAfterSeconds` | `60` | 节点离线判定窗口 |
| `Means:Cluster:DiskHealthIntervalSeconds` | `30` | 磁盘健康检查周期 |

## 请求限制与限流

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Means:RequestLimits:MaxUploadSizeBytes` | `1073741824` | 单次 PUT 或 UploadPart 请求体上限 |
| `Means:RequestLimits:MaxConcurrentUploadRequests` | `64` | PUT/UploadPart 全局并发上限 |
| `Means:RateLimits:Enabled` | `true` | 是否启用固定窗口 API 限流 |
| `Means:RateLimits:ConsoleLoginPermitLimit` | `10` | 登录窗口请求数 |
| `Means:RateLimits:ConsoleLoginWindowSeconds` | `60` | 登录限流窗口 |
| `Means:RateLimits:ConsoleApiPermitLimit` | `600` | Console API 窗口请求数 |
| `Means:RateLimits:ConsoleApiWindowSeconds` | `60` | Console API 窗口 |
| `Means:RateLimits:S3PermitLimit` | `1200` | S3 数据面窗口请求数 |
| `Means:RateLimits:S3WindowSeconds` | `60` | S3 限流窗口 |

限流触发时，S3 返回 XML `SlowDown` 和 `Retry-After`，Console 返回 JSON error。

## Telemetry 与 Console

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Means:Telemetry:Enabled` | `false` | 是否启用 OpenTelemetry tracing |
| `Means:Telemetry:ServiceName` | `Means` | trace service name |
| `Means:Telemetry:ServiceVersion` | 空 | trace service version |
| `Means:Telemetry:OtlpEndpoint` | 空 | OTLP exporter endpoint |
| `Means:Telemetry:SampleRatio` | `1.0` | trace 采样比例 |
| `Means:Console:AdminUser` | `admin` | Console 管理员用户名 |
| `Means:Console:AdminPassword` | `meansadmin` | Console 管理员密码 |
| `Means:Console:SessionHours` | `8` | Cookie session 有效小时 |

非 Development 环境下，如果仍使用默认 Console 用户名或密码，服务会拒绝启动。生产环境也应替换默认 S3 access key/secret key。

## 环境变量示例

ASP.NET Core 环境变量使用双下划线：

```bash
Means__Console__AdminUser=ops-admin
Means__Console__AdminPassword=change-me
Means__Storage__DefaultAccessKey=means-prod
Means__Storage__DefaultSecretKey=change-me-too
Means__S3__ServiceHost=s3.example.com
Means__S3__DomainSuffix=objects.example.com
```
