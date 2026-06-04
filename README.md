# Means

Means 是一个可自部署的 S3-compatible 对象存储服务，当前默认运行时使用 MinIO-inspired `XlFs` 自研存储后端（单节点多盘、对象 manifest、quorum、LogDb 元数据索引），并保留 SQLite + filesystem 作为 legacy/test adapter。

> 截至 2026-05-09：本文档描述的是仓库当前代码状态，而不是仅设计目标。

## 当前实现概览

- 后端：`ASP.NET Core net10.0`（`src/Means`）
- 协议层：S3 兼容地址解析、SigV4 校验、S3 XML 响应（`src/Means.Protocol.S3`）
- 存储层：默认 `XlFs`（`src/Means.Infrastructure.XlFs`），legacy `SQLite(metadata) + filesystem(blob)`（`src/Means.Infrastructure.SqliteFs`）
- 管理面：Cookie 鉴权的 Console API + React 控制台（`/api/console` + `src/Means/wwwroot`）
- SDK：C# SDK、TypeScript SDK（browser-safe）与 Node 扩展

## 已实现能力

### S3 数据面（v1 基线）

支持的操作：

- `ListBuckets`
- `CreateBucket`
- `HeadBucket`
- `DeleteBucket`
- `ListObjectsV2`（`prefix` / `delimiter` / `continuation-token` / `max-keys`）
- `PutObject`
- `GetObject`
- `HeadObject`
- `DeleteObject`
- `CopyObject`（`x-amz-copy-source`，支持 `COPY` / `REPLACE` metadata directive）
- Multipart Upload（initiate / upload part / upload part copy / complete / abort / list parts / list uploads）
- Versioning（`?versioning`、`?versions`、按 `versionId` GET/HEAD/DELETE、delete marker）
- Object tagging（`?tagging`，支持 current version 和指定 `versionId`）
- Lifecycle（`?lifecycle`，支持 expiration、noncurrent cleanup、AbortIncompleteMultipartUpload）
- Bucket CORS（`?cors` 配置 CRUD 和 OPTIONS preflight）
- Bucket notification（`?notification` 配置持久化预留接口）
- SigV4 预签名 URL（`GET` / `PUT` / multipart `UploadPart`）
- `?policy` 子资源（`GET` / `PUT` / `DELETE`）

实现细节：

- 同时支持 path-style 与 virtual-hosted-style 地址解析。
- 统一返回 S3 风格 XML（列表、错误、复制结果）。
- 支持 Range 读取（`206`），非法范围返回 `416 InvalidRange` 且带 `Content-Range`。
- 响应支持内容协商压缩（`br` / `gzip`），Range 请求下禁用压缩。
- `PutObject` 为原子写入语义：对象在元数据事务提交后对外可见。
- Multipart Upload 使用 S3 严格规则：`partNumber` 为 `1..10000`，除最后一片外每片至少 `5 MiB`，完成后的 ETag 为 `md5(concat(part-md5-bytes))-part-count`。
- `Means:RequestLimits:MaxUploadSizeBytes` 对 multipart 表示单个 part 的请求体限制，不限制完成后的总对象大小。

### Console 管理面

内置 `/api/console`（JSON API）与 Web 控制台，覆盖：

- 登录/登出/会话检查（Cookie）
- Bucket 管理（创建、删除、对象浏览）
- Bucket Policy 管理
- 预签名上传/下载链接生成
- 大文件分片上传（默认 5 MiB 起启用 multipart，16 MiB part，3 并发）
- AccessKey 管理（创建、删除、列表）
- 系统设置（最大上传大小）
- 审计日志与小时级请求统计看板
- 集群状态页、节点/磁盘健康页与诊断导出（`/api/console/cluster`、`/api/console/diagnostics`）
- Prometheus 文本指标导出（`/metrics`）与可选 OpenTelemetry tracing（OTLP）
- 后台任务统一管理与手动触发（heartbeat、disk health、metadata consistency、storage GC、repair、rebalance、lifecycle、replication worker）
- API 固定窗口限流（Console 登录、Console API、S3 数据面）

## 尚未实现（当前代码无此能力）

- Replication
- Object Lock / Retention
- IAM / STS 完整模型
- 多节点分布式数据分片/纠删码

## 快速开始

### 1) 环境要求

- .NET SDK 10
- Node.js 20+（仅在你需要修改前端时）

### 2) 启动服务

```bash
dotnet restore Means.slnx
dotnet build Means.slnx
dotnet run --project src/Means/Means.csproj --launch-profile http
```

默认开发地址：`http://localhost:5178`

### 2.1) Docker Compose 启动

默认单机 XlFs 版本：

```bash
docker compose up -d --build
```

访问地址：`http://localhost:5178`

默认 Compose 控制台账号：`meansadmin` / `meansadmin-local`。默认 S3 凭证：`meansadmin` / `meansadmin-local-secret`。生产部署应通过 `.env` 或环境变量覆盖这些值。

多节点实验拓扑：

```bash
docker compose -f compose.multinode.yaml up -d --build
```

访问地址：

- `http://localhost:5181`：`means-node1`
- `http://localhost:5182`：`means-node2`
- `http://localhost:5183`：`means-node3`

注意：当前多节点 compose 用于节点/磁盘拓扑和运维页面验证，每个节点仍拥有独立 XlFs 命名空间；在分布式 metadata/RPC 完成前，不要在这些节点前放数据面负载均衡器。

### 3) 访问控制台

打开 `http://localhost:5178`，使用默认开发账号：

- 用户名：`admin`
- 密码：`meansadmin`

### 4) 本地 S3 访问方式

开发环境可直接使用同源别名路径：

- `http://localhost:5178/s3/{bucket}/{key}`

也支持标准主机风格（需本机 DNS/hosts 配置）：

- Path-style：`http(s)://api.means.local/{bucket}/{key}`
- Virtual-hosted-style：`http(s)://{bucket}.means.local/{key}`

## 配置说明（`src/Means/appsettings.json`）

| 配置键 | 默认值 | 说明 |
| --- | --- | --- |
| `Means:S3:ServiceHost` | `api.means.local` | Path-style 主机名 |
| `Means:S3:DomainSuffix` | `means.local` | Virtual-hosted-style 域后缀 |
| `Means:S3:AliasPrefix` | `/s3` | 同源 S3 别名前缀 |
| `Means:Storage:Backend` | `XlFs` | 存储后端；`XlFs` 为默认，`SqliteFs` 仅作为 legacy/test adapter |
| `Means:Storage:DatabasePath` | `data/means.db` | legacy SQLite 元数据库路径；XlFs 仅用它检测旧数据，不自动迁移 |
| `Means:Storage:ObjectsPath` | `data/objects` | 未配置 `Disks` 时的 XlFs 单盘目录/legacy 对象目录 |
| `Means:Storage:Disks` | `/data/xlfs/disk1...` | XlFs 多盘根目录，每块盘会写入 `.means.sys/format.json` |
| `Means:Storage:ErasureDataShards` | `2` | XlFs EC profile 数据分片配置；首期数据路径为 full-copy quorum |
| `Means:Storage:ErasureParityShards` | `2` | XlFs EC profile 校验分片配置；首期数据路径为 full-copy quorum |
| `Means:Storage:WriteQuorum` | `3` | XlFs 写成功 quorum |
| `Means:Storage:ReadQuorum` | `1` | XlFs 读 quorum |
| `Means:Storage:MetaSyncMode` | `Always` | XlFs metadata WAL flush 策略 |
| `Means:Storage:AllowNewFormatWithExistingSqlite` | `false` | 检测到旧 SQLite 文件且没有 XlFs format 时，是否允许直接启动新命名空间 |
| `Means:Storage:VerifyChecksumOnRead` | `false` | XlFs/legacy 读取时是否同步校验 SHA256；开启会提升数据校验强度但会增加一次完整读 I/O |
| `Means:Storage:DefaultAccessKey` | `meansadmin` | 首次初始化的默认 AccessKey |
| `Means:Storage:DefaultSecretKey` | `meansadminsecret` | 首次初始化的默认 SecretKey |
| `Means:Storage:MultipartUploadCleanupAgeHours` | `24` | 未完成 Multipart Upload 超过该时长后可被后台清理 |
| `Means:Storage:MultipartUploadCleanupIntervalMinutes` | `60` | 未完成 Multipart Upload 后台清理间隔 |
| `Means:Storage:GarbageCollectionIntervalSeconds` | `3600` | 存储孤儿文件 GC 后台扫描间隔 |
| `Means:Storage:GarbageCollectionBatchSize` | `1000` | 每次 GC 最多处理的候选文件数 |
| `Means:Storage:GarbageCollectionTempFileAgeMinutes` | `60` | 未引用文件和临时文件超过该年龄后才允许被 GC 清理 |
| `Means:Storage:ReplicationIntervalSeconds` | `3600` | replication worker 预留后台任务间隔；未配置复制规则时只上报任务状态 |
| `Means:RequestLimits:MaxUploadSizeBytes` | `1073741824` | 默认最大上传体积（1 GiB） |
| `Means:RequestLimits:MaxConcurrentUploadRequests` | `64` | S3 `PUT` / multipart `UploadPart` 全局并发上限，超过后返回 `503 SlowDown` |
| `Means:RateLimits:Enabled` | `true` | 是否启用 API 固定窗口限流 |
| `Means:RateLimits:ConsoleLoginPermitLimit` | `10` | Console 登录窗口内允许请求数 |
| `Means:RateLimits:ConsoleLoginWindowSeconds` | `60` | Console 登录限流窗口秒数 |
| `Means:RateLimits:ConsoleApiPermitLimit` | `600` | Console API 窗口内允许请求数 |
| `Means:RateLimits:ConsoleApiWindowSeconds` | `60` | Console API 限流窗口秒数 |
| `Means:RateLimits:S3PermitLimit` | `1200` | S3 数据面窗口内允许请求数 |
| `Means:RateLimits:S3WindowSeconds` | `60` | S3 数据面限流窗口秒数 |
| `Means:Telemetry:Enabled` | `false` | 是否启用 OpenTelemetry tracing |
| `Means:Telemetry:ServiceName` | `Means` | tracing resource service name |
| `Means:Telemetry:OtlpEndpoint` | `` | OTLP exporter endpoint；为空时不导出到 collector |
| `Means:Telemetry:SampleRatio` | `1.0` | trace 采样比例，范围 `0..1` |
| `Means:Console:AdminUser` | `admin` | 控制台管理员用户名 |
| `Means:Console:AdminPassword` | `meansadmin` | 控制台管理员密码 |
| `Means:Console:SessionHours` | `8` | 控制台会话有效时长（小时） |

注意：

- 生产环境下，如果仍使用默认控制台账号密码，服务会在启动时拒绝运行。
- 生产环境应同时替换默认 AccessKey/SecretKey。
- 默认 `XlFs` 不会自动迁移旧 SQLite 数据；如果 `DatabasePath` 已存在且磁盘没有 `.means.sys/format.json`，启动会失败并提示使用 export/import 或显式设置 `AllowNewFormatWithExistingSqlite=true`。
- 未完成的 Multipart Upload 会由后台任务自动清理元数据和 part 文件；前端取消/失败仍会 best-effort 调用 abort，但磁盘释放不依赖浏览器请求一定成功。
- Storage GC 只扫描受控存储目录和已注册磁盘目录；XlFs 按 `MeansLogDb`/`xl.meta` 引用集判断 orphan 文件，legacy SQLite 按 SQLite 元数据引用集判断；未超过年龄保护的文件不会删除。
- 默认读取路径不在返回对象前同步扫全文件做 SHA256，避免大对象 GET 双倍磁盘 I/O；需要强读时校验时可开启 `VerifyChecksumOnRead`，后台 scrub 仍会做校验和修复入队。
- S3 单次 `PutObject` 和 multipart `UploadPart` 会共享上传并发限制；超过限制返回 S3 XML 错误 `SlowDown` 和 `Retry-After: 1`。
- Console 登录、Console API 和 S3 数据面都支持固定窗口 API 限流；超过限制时 Console 返回 JSON `SlowDown`，S3 返回 XML `SlowDown`，并带 `Retry-After`。
- Prometheus 可抓取 `/metrics`；推荐告警规则见 `docs/operations/deployment-observability.md`。
- OpenTelemetry 默认关闭；设置 `Means:Telemetry:Enabled=true` 后会采集 ASP.NET Core 请求和 Means 后台任务 span，配置 `Means:Telemetry:OtlpEndpoint` 后导出到 collector。

## 开发与测试

### 运行测试

```bash
dotnet test Means.slnx
```

当前测试覆盖：

- `Means.UnitTests`：地址解析、命名规则、策略判定、压缩规则
- `Means.IntegrationTests`：S3 全链路、预签名、Console API 工作流
- `Means.ContractTests`：SDK 协议 YAML 与 fixtures 完整性

### 前端开发

```bash
cd web
npm install
npm run dev
```

- Vite 开发服务器默认代理 `/api` 与 `/s3` 到 `http://localhost:5178`
- 构建命令 `npm run build` 会把前端产物输出到 `src/Means/wwwroot`

## SDK 与规范

- C# SDK：`SDKs/csharp`
- TypeScript SDK（browser-safe）：`SDKs/typescript/packages/sdk`
- TypeScript Node 扩展（SigV4 签名/预签名）：`SDKs/typescript/packages/sdk-node`
- 机器可读协议规范：`SDKs/spec/means-sdk-v1.yaml`
- 人类可读协议文档：`SDKs/spec/means-sdk-v1.md`

## License

MIT，见 [LICENSE](LICENSE)。
