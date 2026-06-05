# 开发、测试与路线图

本文档面向贡献者，说明仓库结构、构建测试命令、测试覆盖、兼容验证和当前路线图边界。

## 仓库结构

```text
src/
  Means/                         ASP.NET Core host, endpoints, middleware, services
  Means.Core/                    Core models, requests, abstractions, policy evaluator
  Means.Protocol.S3/             S3 addressing, SigV4, XML, compression, validation
  Means.Infrastructure.XlFs/     Default storage backend
SDKs/
  csharp/                        Means.Client
  typescript/packages/sdk        Browser-safe shared SDK
  typescript/packages/sdk-node   Node signing extension
  spec/                          Machine-readable SDK contract and fixtures
  examples/                      Runnable SDK examples
tests/
  Means.UnitTests
  Means.IntegrationTests
  Means.ContractTests
web/                             React/Vite Console
docs/                            Maintainer-facing Markdown docs
docs-site/                       Docs website source, currently untracked in this worktree
```

## 构建

后端：

```bash
dotnet restore Means.slnx
dotnet build Means.slnx
```

C# SDK：

```bash
dotnet build SDKs/csharp/Means.Client.csproj
dotnet pack SDKs/csharp/Means.Client.csproj -c Release
```

TypeScript SDK：

```bash
cd SDKs/typescript
npm install
npm run build
```

Web Console：

```bash
cd web
npm install
npm run build
```

## 测试

全部测试：

```bash
dotnet test Means.slnx
```

测试项目：

| 项目 | 关注点 |
| --- | --- |
| `Means.UnitTests` | 地址解析、bucket policy、placement、XlFs store 行为 |
| `Means.IntegrationTests` | S3 endpoint、Console API、multipart、versioning、lifecycle、metrics、diagnostics |
| `Means.ContractTests` | SDK spec YAML、fixtures、合同完整性 |

兼容脚本：

```bash
scripts/compatibility/run-s3-client-matrix.ps1
scripts/benchmarks/means-s3-benchmark.ps1
```

## 重要测试场景

S3 endpoint integration tests 覆盖：

- path-style 和 virtual-hosted-style。
- bucket/object lifecycle。
- Range GET 与压缩协商。
- SigV4 header 和 query presign。
- 无效 bucket/key/range 的 XML error。
- Multipart initiate/upload/list/complete/abort。
- UploadPartCopy。
- Versioning、delete marker、versionId。
- Object tagging。
- Bucket lifecycle。
- Bucket CORS。
- Request body limit 和 upload concurrency SlowDown。

Console API tests 覆盖：

- 登录/session。
- bucket/object 管理。
- presigned upload/download。
- 浏览器 multipart 辅助流程。
- access key 创建、删除和失效验证。
- settings、audit、dashboard stats。
- cluster、diagnostics、EC profiles。
- background task 管理和手动触发。
- `/metrics` 输出。

## 开发注意事项

- S3 数据面错误必须保持 XML；Console API 错误必须保持 JSON。
- 浏览器 SDK 不能加入 SecretKey 签名能力。
- 修改 S3 操作时同步更新 `SDKs/spec/means-sdk-v1.yaml`、SDK 和 contract tests。
- 修改配置时同步更新 `docs/reference/configuration.md`。
- 修改指标时同步更新 `docs/operations/deployment-observability.md`。
- 修改存储一致性或后台任务时同步更新 `docs/architecture/storage-and-consistency.md`。

## 当前已完成能力

- ASP.NET Core S3-compatible 数据面。
- XlFs 默认存储后端。
- Bucket/Object CRUD。
- ListObjectsV2。
- Range GET。
- SigV4 header/query presign。
- Basic bucket policy。
- Multipart upload。
- Versioning、ListObjectVersions、delete marker。
- Object tagging。
- Lifecycle。
- CORS。
- Notification 配置持久化预留。
- Console API 和 React 控制台。
- Access key 管理。
- Metrics、OpenTelemetry tracing 配置。
- 后台任务统一管理。
- C# SDK、TypeScript browser SDK、TypeScript Node SDK。

## 当前限制和后续方向

| 方向 | 状态 |
| --- | --- |
| Reed-Solomon EC 写入/读取/重建 | 初版完成：XlFs 普通 `PutObject` 与 multipart `UploadPart` 支持本地/跨节点 EC shard、降级写入、读恢复和后台 repair |
| 真实多节点分布式 metadata/RPC | 部分完成：节点 heartbeat/topology、单 pool 对象放置、内部 shard RPC、跨节点 full-copy/EC 写入与修复已落地；生产级共享 metadata、erasure set/故障域和准入校验仍需完善 |
| 生产级 rebalance 和数据迁移 | 部分完成：已有基于当前 placement plan 的 shard/manifest 迁移和回滚；仍需扩缩容策略、限速、长任务进度和更大规模故障注入 |
| IAM/STS | 未完成 |
| Policy condition | 未完成 |
| Access key rotation | 未完成 |
| Object Lock/Retention/Legal Hold | 未完成 |
| Replication 规则和 worker | 预留 |
| Bucket notification 事件投递 | 预留 |
| SSE-S3/SSE-KMS | 未完成 |
| 管理面 RBAC | 未完成 |
| small object packing | 暂不实现，待 inode 或小对象规模成为瓶颈后再评估 |

## 里程碑建议

1. 单节点生产化 hardening：配置校验、备份恢复演练、兼容矩阵稳定。
2. 多副本分布式存储：共享 metadata、节点间 RPC、读写 quorum 语义和跨节点诊断。
3. EC 和 repair/rebalance：完善自动修复、迁移限速、扩缩容和故障注入。
4. 安全模型：IAM/STS、access key rotation、RBAC、SSE。
5. 数据治理：Object Lock、retention、replication、quota。
