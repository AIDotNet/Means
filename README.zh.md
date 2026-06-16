# Means

> **S3 兼容对象存储 — 企业级、可自部署、基于 .NET 构建。**

Means 是一个可自部署的 S3 兼容对象存储服务，基于 ASP.NET Core 构建。默认运行时使用 MinIO 架构启发的 `XlFs` 自研存储后端（单节点多盘、对象 manifest、quorum 写入、LogDb 元数据索引），完全采用自研 **MeansLogDb** 元数据引擎。

> **截至 2026-05-09：** 本文档反映的是仓库**当前代码状态**，而非仅设计目标。

---

## 目录

- [架构概览](#架构概览)
- [已实现能力](#已实现能力)
  - [S3 数据面](#s3-数据面v1-基线)
  - [Console 管理面](#console-管理面)
- [尚未实现](#尚未实现)
- [快速开始](#快速开始)
  - [环境要求](#1-环境要求)
  - [启动服务](#2-启动服务)
  - [Docker Compose 启动](#21-docker-compose-启动)
  - [访问控制台](#3-访问控制台)
  - [S3 访问方式](#4-本地-s3-访问方式)
- [配置参考](#配置参考)
- [开发与测试](#开发与测试)
  - [运行测试](#运行测试)
  - [前端开发](#前端开发)
- [SDK 与规范](#sdk-与规范)
- [项目地图](#项目地图)
- [许可证](#license)

---

## 架构概览

| 层级 | 项目 | 用途 |
|---|---|---|
| **主机 / API** | `src/Means` | ASP.NET Core `net10.0` 应用入口、中间件管道、端点、DI 组合 |
| **协议层** | `src/Means.Protocol.S3` | S3 兼容地址解析、SigV4 签名校验、S3 XML 响应序列化 |
| **存储引擎** | `src/Means.Infrastructure.XlFs` | 默认 XlFs 存储后端（多盘、manifest、quorum、MeansLogDb 元数据） |
| **核心抽象** | `src/Means.Core` | 领域模型、存储接口、策略、错误定义、放置策略 |
| **管理 API** | `src/Means/Endpoints/Console` | Cookie 鉴权的 Console JSON API |
| **Web UI** | `src/Means/wwwroot` | 基于 React 的管理面板（从 `web/` 构建） |
| **SDK - C#** | `SDKs/csharp` | 全功能 C# 客户端 SDK |
| **SDK - TypeScript** | `SDKs/typescript` | 浏览器安全 TS SDK + Node 扩展（SigV4） |
| **测试** | `tests/` | 单元测试、集成测试、契约测试 |

### 解决方案结构（`Means.slnx`）

```
Means.slnx
├── src/
│   ├── Means/                  # 主机应用
│   ├── Means.Core/             # 核心抽象与领域
│   ├── Means.Infrastructure.XlFs/  # XlFs 存储引擎
│   └── Means.Protocol.S3/      # S3 协议实现
│
├── tests/
│   ├── Means.UnitTests/        # 单元测试
│   ├── Means.IntegrationTests/ # 集成测试
│   └── Means.ContractTests/    # SDK 规范合规测试
│
├── SDKs/
│   ├── csharp/                 # C# SDK
│   ├── typescript/             # TypeScript SDK
│   └── spec/                   # 机器可读协议规范
│
├── web/                        # React 前端（Vite）
└── docs/                       # 文档站点（Next.js）
```

---

## 已实现能力

### S3 数据面（v1 基线）

| 分类 | 操作 | 状态 |
|---|---|---|
| **服务** | `ListBuckets` | ✅ |
| **存储桶** | `CreateBucket`, `HeadBucket`, `DeleteBucket` | ✅ |
| **对象** | `PutObject`, `GetObject`, `HeadObject`, `DeleteObject` | ✅ |
| **列表** | `ListObjectsV2`（`prefix`, `delimiter`, `continuation-token`, `max-keys`） | ✅ |
| **复制** | `CopyObject`（`x-amz-copy-source`，支持 `COPY`/`REPLACE` metadata directive） | ✅ |
| **分片上传** | Initiate, UploadPart, UploadPartCopy, Complete, Abort, ListParts, ListMultipartUploads | ✅ |
| **版本控制** | `?versioning`, `?versions`, 按 `versionId` GET/HEAD/DELETE, delete markers | ✅ |
| **对象标签** | `?tagging`（当前版本及指定 `versionId`） | ✅ |
| **生命周期** | `?lifecycle`（过期、noncurrent 清理、AbortIncompleteMultipartUpload） | ✅ |
| **CORS** | `?cors` 配置 CRUD、OPTIONS preflight | ✅ |
| **通知** | `?notification` 配置持久化（预留接口） | ✅ |
| **策略** | `?policy` 子资源（`GET`/`PUT`/`DELETE`） | ✅ |
| **预签名 URL** | SigV4 预签名 `GET`、`PUT`、multipart `UploadPart` | ✅ |

#### 关键实现细节

- **地址解析：** 同时支持 `path-style` 和 `virtual-hosted-style` 两种 S3 寻址方式。
- **响应格式：** 统一返回 S3 兼容 XML（列表、错误、复制结果）。
- **Range 读取：** `Range` 头 → `206 Partial Content`；非法范围 → `416 InvalidRange` 并带 `Content-Range` 头。
- **压缩：** 响应支持内容协商压缩（`br` / `gzip`），Range 请求下禁用。
- **原子写入：** `PutObject` 为原子语义——对象仅在元数据事务提交后才对外可见。
- **Multipart 规则：**
  - `partNumber`：`1`–`10000`
  - 最小分片大小（除最后一片）：`5 MiB`
  - 最终 ETag：`md5(concat(part-md5-bytes))-part-count`
  - `Means:RequestLimits:MaxUploadSizeBytes` 对 multipart 表示单个 part 的请求体限制，不限制总对象大小。
- **并发控制：** S3 `PUT` 和 multipart `UploadPart` 共享全局并发限制（默认 64），超限返回 `503 SlowDown` 并带 `Retry-After: 1`。
- **校验和验证：** 读取时默认关闭 SHA256 验证以避免大对象双倍 I/O。可通过 `VerifyChecksumOnRead` 开启。后台 scrub 始终进行校验和并加入修复队列。

### Console 管理面

内置管理 API（`/api/console`，JSON 格式）和 Web 管理面板：

| 功能 | 端点 / 说明 |
|---|---|
| **身份认证** | 基于 Cookie 的登录/登出/会话检查 |
| **存储桶管理** | 创建、删除、对象浏览 |
| **存储桶策略** | 查看和编辑 Bucket Policy |
| **预签名 URL** | 生成上传/下载链接 |
| **大文件上传** | 5 MiB 起启用 multipart，16 MiB 分片，3 并发 |
| **AccessKey 管理** | 创建、删除、列出访问密钥 |
| **系统设置** | 配置最大上传大小 |
| **审计与指标** | 审计日志、小时级请求统计看板 |
| **集群状态** | 节点/磁盘健康页、诊断导出（`/api/console/cluster`, `/api/console/diagnostics`） |
| **监控导出** | Prometheus `/metrics`、可选 OpenTelemetry（OTLP） |
| **后台任务** | 统一管理及手动触发——heartbeat、disk health、metadata consistency、storage GC、repair、rebalance、lifecycle、replication worker |
| **速率限制** | Console 登录、Console API、S3 数据面均支持固定窗口限流 |

---

## 尚未实现

以下功能**当前代码中未实现**：

- 🔲 **Replication** — 跨存储桶/存储桶复制规则
- 🔲 **Object Lock / Retention** — WORM 合规
- 🔲 **IAM / STS** — 完整的身份与访问管理模型
- 🔲 **分布式分片 / 纠删码** — 多节点数据分片和跨节点纠删码

---

## 快速开始

### 1) 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | `10.0` | 构建和运行所需 |
| Node.js | `20+` | 仅在前端开发时需要 |

### 2) 启动服务

```bash
# 还原依赖
dotnet restore Means.slnx

# 构建解决方案
dotnet build Means.slnx

# 使用 http launch profile 运行
dotnet run --project src/Means/Means.csproj --launch-profile http
```

开发服务器默认地址：**`http://localhost:5178`**

### 2.1) Docker Compose 启动

**单节点（默认 XlFs）：**

```bash
docker compose up -d --build
```

访问地址：`http://localhost:5178`

默认凭据（生产环境请通过 `.env` 或环境变量覆盖）：
- 控制台：`meansadmin` / `meansadmin-local`
- S3：`meansadmin` / `meansadmin-local-secret`

**多节点（实验性）：**

```bash
docker compose -f compose.multinode.yaml up -d --build
```

| 节点 | 地址 |
|---|---|
| `means-node1` | `http://localhost:5181` |
| `means-node2` | `http://localhost:5182` |
| `means-node3` | `http://localhost:5183` |

> **⚠️ 重要：** 多节点 Compose 仅用于节点/磁盘拓扑和运维页面验证。每个节点拥有独立的 XlFs 命名空间。在分布式 metadata/RPC 完成之前，**不要**在这些节点前放置数据面负载均衡器。

### 3) 访问控制台

打开 `http://localhost:5178`，使用默认开发账号登录：

| 字段 | 值 |
|---|---|
| 用户名 | `admin` |
| 密码 | `meansadmin` |

### 4) 本地 S3 访问方式

**同源别名路径（开发环境）：**

```
http://localhost:5178/s3/{bucket}/{key}
```

**标准主机风格（需配置 DNS/hosts）：**

| 风格 | URL 模式 |
|---|---|
| Path-style | `http(s)://api.means.local/{bucket}/{key}` |
| Virtual-hosted-style | `http(s)://{bucket}.means.local/{key}` |

---

## 配置参考

所有配置项位于 `src/Means/appsettings.json` 的 `Means` 节下。

### S3 设置

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:S3:ServiceHost` | `api.means.local` | Path-style 主机名 |
| `Means:S3:DomainSuffix` | `means.local` | Virtual-hosted-style 域后缀 |
| `Means:S3:AliasPrefix` | `/s3` | 同源 S3 别名前缀 |

### 存储设置

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:Storage:ObjectsPath` | `data/objects` | 未配置 `Disks` 时的单盘目录 |
| `Means:Storage:Disks` | `/data/xlfs/disk1...` | XlFs 多盘根目录，每盘写入 `.means.sys/format.json` |
| `Means:Storage:ErasureDataShards` | `2` | Reed-Solomon 数据分片数；在线磁盘满足 data+parity 时启用 |
| `Means:Storage:ErasureParityShards` | `2` | Reed-Solomon 校验分片数；磁盘不足时回退至 full-copy quorum |
| `Means:Storage:WriteQuorum` | `3` | 写入成功所需最少磁盘数 |
| `Means:Storage:ReadQuorum` | `1` | 读取成功所需最少磁盘数 |
| `Means:Storage:MetaSyncMode` | `Always` | 元数据 WAL flush 策略 |
| `Means:Storage:VerifyChecksumOnRead` | `false` | 读取时同步校验 SHA256（增加一次完整 I/O） |
| `Means:Storage:DefaultAccessKey` | `meansadmin` | 默认 S3 AccessKey |
| `Means:Storage:DefaultSecretKey` | `meansadminsecret` | 默认 S3 SecretKey |
| `Means:Storage:MultipartUploadCleanupAgeHours` | `24` | 未完成分片上传的清理年龄阈值 |
| `Means:Storage:MultipartUploadCleanupIntervalMinutes` | `60` | 分片上传清理后台任务间隔 |
| `Means:Storage:GarbageCollectionIntervalSeconds` | `3600` | 孤儿文件 GC 扫描间隔 |
| `Means:Storage:GarbageCollectionBatchSize` | `1000` | 每次 GC 最多处理的候选文件数 |
| `Means:Storage:GarbageCollectionTempFileAgeMinutes` | `60` | 临时/未引用文件可被 GC 的最小年龄 |
| `Means:Storage:ReplicationIntervalSeconds` | `3600` | replication worker 间隔（未配置规则时仅上报状态） |

### 请求限制

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:RequestLimits:MaxUploadSizeBytes` | `1073741824`（1 GiB） | 单次请求最大上传大小 |
| `Means:RequestLimits:MaxConcurrentUploadRequests` | `64` | `PUT`/`UploadPart` 全局并发上限，超限返回 `503 SlowDown` |

### 速率限制

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:RateLimits:Enabled` | `true` | 启用 API 固定窗口限流 |
| `Means:RateLimits:ConsoleLoginPermitLimit` | `10` | Console 登录窗口内允许请求数 |
| `Means:RateLimits:ConsoleLoginWindowSeconds` | `60` | Console 登录限流窗口（秒） |
| `Means:RateLimits:ConsoleApiPermitLimit` | `600` | Console API 窗口内允许请求数 |
| `Means:RateLimits:ConsoleApiWindowSeconds` | `60` | Console API 限流窗口（秒） |
| `Means:RateLimits:S3PermitLimit` | `1200` | S3 数据面窗口内允许请求数 |
| `Means:RateLimits:S3WindowSeconds` | `60` | S3 数据面限流窗口（秒） |

### 遥测

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:Telemetry:Enabled` | `false` | 启用 OpenTelemetry tracing |
| `Means:Telemetry:ServiceName` | `Means` | Tracing resource service name |
| `Means:Telemetry:OtlpEndpoint` | *（空）* | OTLP exporter 端点 |
| `Means:Telemetry:SampleRatio` | `1.0` | Trace 采样比例 `[0, 1]` |

### 集群

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:Cluster:InternalAuthToken` | *（空）* | 节点间 shard RPC token；为空时 `/api/internal/cluster` 返回 404 |
| `Means:Cluster:MaxShardTransferBytes` | `5368709120`（5 GiB） | 单 shard 流式传输上限 |

### 控制台

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `Means:Console:AdminUser` | `admin` | 管理员用户名 |
| `Means:Console:AdminPassword` | `meansadmin` | 管理员密码 |
| `Means:Console:SessionHours` | `8` | 会话有效时长（小时） |

### 重要说明

- **生产安全：** 服务检测到默认控制台凭据时会拒绝启动。生产环境务必替换 `AdminUser`/`AdminPassword` 及 `DefaultAccessKey`/`DefaultSecretKey`。
- **分片上传清理：** 后台任务自动清理未完成上传的元数据和 part 文件。前端取消会尽力调用 abort，但磁盘释放不依赖浏览器请求成功。
- **读取校验：** 默认读取路径不做 SHA256 校验以避免大对象双倍 I/O。开启 `VerifyChecksumOnRead` 启用强校验。后台 scrub 始终进行校验和检查。
- **限流响应：** Console 返回 JSON `SlowDown`；S3 返回 XML `SlowDown`；均包含 `Retry-After` 头。
- **Prometheus：** 可抓取 `/metrics`。推荐告警规则见 `docs/operations/deployment-observability.md`。
- **OpenTelemetry：** 默认关闭。设置 `Means:Telemetry:Enabled=true` 采集 ASP.NET Core 请求和后台任务 span。配置 `OtlpEndpoint` 导出到 collector。

---

## 开发与测试

### 运行测试

```bash
dotnet test Means.slnx
```

| 测试项目 | 测试重点 |
|---|---|
| `Means.UnitTests` | 地址解析、命名规则、策略判定、压缩逻辑 |
| `Means.IntegrationTests` | S3 全链路工作流、预签名 URL、Console API 端到端 |
| `Means.ContractTests` | SDK 协议 YAML 规范与 fixture 完整性 |

### 前端开发

```bash
cd web
npm install
npm run dev
```

- Vite 开发服务器默认代理 `/api` 和 `/s3` 到 `http://localhost:5178`
- 生产构建：`npm run build` → 输出到 `src/Means/wwwroot`

---

## SDK 与规范

| 包 | 位置 | 说明 |
|---|---|---|
| **C# SDK** | `SDKs/csharp` | 全功能 .NET 客户端 SDK（`Means.Client.csproj`） |
| **TypeScript SDK** | `SDKs/typescript/packages/sdk` | 浏览器安全的 S3 客户端 |
| **TypeScript Node 扩展** | `SDKs/typescript/packages/sdk-node` | 适用于 Node.js 的 SigV4 签名与预签名 |
| **协议规范（YAML）** | `SDKs/spec/means-sdk-v1.yaml` | 机器可读 API 规范 |
| **协议规范（Markdown）** | `SDKs/spec/means-sdk-v1.md` | 人类可读协议文档 |
| **SDK 示例** | `SDKs/examples/` | C# 和 TypeScript 使用示例 |

---

## 项目地图

```
Means/
├── CHANGELOG.md
├── IMPLEMENTATION_PLAN.md
├── Means.slnx                      # .NET 解决方案文件
├── compose.yaml                    # 单节点 Docker Compose
├── compose.multinode.yaml          # 多节点实验性 Compose
├── README.md                       # 本文档（英文版）
├── README.zh.md                    # 中文文档
│
├── src/
│   ├── Means/                      # ASP.NET Core 主机
│   │   ├── Program.cs              # 应用入口
│   │   ├── Composition/            # DI 组合与服务注册
│   │   ├── Configuration/          # 配置绑定与验证
│   │   ├── Endpoints/              # HTTP API 端点（Console 等）
│   │   ├── Middleware/             # ASP.NET Core 中间件管道
│   │   ├── Services/               # 应用服务
│   │   ├── Security/               # 认证与授权
│   │   ├── Serialization/          # JSON 序列化配置
│   │   ├── Properties/             # 启动配置文件、程序集信息
│   │   └── wwwroot/                # 构建后的前端产物
│   │
│   ├── Means.Core/                 # 核心领域与抽象
│   │   ├── Abstractions/           # 存储接口与契约
│   │   ├── Constants/              # 常用常量
│   │   ├── Errors/                 # 领域错误类型
│   │   ├── Models/                 # 领域模型
│   │   ├── Placement/              # 磁盘放置策略
│   │   ├── Policies/               # 存储策略
│   │   └── Requests/               # 请求模型定义
│   │
│   ├── Means.Infrastructure.XlFs/  # XlFs 存储引擎
│   │   ├── Store/                  # 核心存储实现
│   │   ├── LogDb/                  # MeansLogDb 元数据引擎
│   │   ├── Models/                 # XlFs 特定模型
│   │   └── XlFsOptions.cs          # 存储选项
│   │
│   └── Means.Protocol.S3/         # S3 协议层
│       ├── Addressing/             # Path-style 与 vhost-style 解析
│       ├── Compression/            # 内容协商与压缩
│       ├── Serialization/          # S3 XML 响应序列化
│       ├── Signing/                # SigV4 签名验证
│       └── Validation/             # 请求验证
│
├── tests/
│   ├── Means.UnitTests/
│   ├── Means.IntegrationTests/
│   └── Means.ContractTests/
│
├── SDKs/
│   ├── csharp/
│   ├── typescript/
│   ├── spec/
│   └── examples/
│
├── web/                            # React 前端（Vite + TypeScript）
│   ├── src/                        # React 组件与页面
│   ├── public/                     # 静态资源
│   ├── vite.config.ts              # Vite 配置
│   └── package.json
│
├── docs/                           # 文档站点（Next.js）
│   ├── content/docs/               # 文档内容
│   ├── app/                        # Next.js App Router 页面
│   └── source.config.ts
│
└── scripts/                        # 工具脚本
    ├── benchmarks/                 # S3 基准测试脚本
    └── compatibility/              # S3 客户端兼容性矩阵
```

---

## 许可证

MIT — 详见 [LICENSE](LICENSE)。