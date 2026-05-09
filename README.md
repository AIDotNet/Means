# Means

Means 是一个可自部署的 S3-compatible 对象存储服务，当前仓库已经提供可运行的单机基线实现（ASP.NET Core + SQLite + 本地文件系统 + 内置 Web Console）。

> 截至 2026-05-09：本文档描述的是仓库当前代码状态，而不是仅设计目标。

## 当前实现概览

- 后端：`ASP.NET Core net10.0`（`src/Means`）
- 协议层：S3 兼容地址解析、SigV4 校验、S3 XML 响应（`src/Means.Protocol.S3`）
- 存储层：`SQLite(metadata) + filesystem(blob)`（`src/Means.Infrastructure.SqliteFs`）
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
- `CopyObject`（`x-amz-copy-source`）
- Multipart Upload（initiate / upload part / complete / abort / list parts / list uploads）
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

## 尚未实现（当前代码无此能力）

- Versioning
- Lifecycle
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
| `Means:Storage:DatabasePath` | `data/means.db` | SQLite 元数据库路径 |
| `Means:Storage:ObjectsPath` | `data/objects` | 对象数据目录 |
| `Means:Storage:DefaultAccessKey` | `meansadmin` | 首次初始化的默认 AccessKey |
| `Means:Storage:DefaultSecretKey` | `meansadminsecret` | 首次初始化的默认 SecretKey |
| `Means:Storage:MultipartUploadCleanupAgeHours` | `24` | 未完成 Multipart Upload 超过该时长后可被后台清理 |
| `Means:Storage:MultipartUploadCleanupIntervalMinutes` | `60` | 未完成 Multipart Upload 后台清理间隔 |
| `Means:RequestLimits:MaxUploadSizeBytes` | `1073741824` | 默认最大上传体积（1 GiB） |
| `Means:Console:AdminUser` | `admin` | 控制台管理员用户名 |
| `Means:Console:AdminPassword` | `meansadmin` | 控制台管理员密码 |
| `Means:Console:SessionHours` | `8` | 控制台会话有效时长（小时） |

注意：

- 生产环境下，如果仍使用默认控制台账号密码，服务会在启动时拒绝运行。
- 生产环境应同时替换默认 AccessKey/SecretKey。
- 未完成的 Multipart Upload 会由后台任务自动清理元数据和 part 文件；前端取消/失败仍会 best-effort 调用 abort，但磁盘释放不依赖浏览器请求一定成功。

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
