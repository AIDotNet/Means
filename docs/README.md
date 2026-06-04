# Means 文档索引

本文档集基于当前仓库代码重新整理，覆盖服务端架构、S3 协议、存储一致性、SDK 使用、部署运维、测试与路线图。旧的散落文档已合并到以下分类中。

## 快速入口

- [架构总览](architecture/overview.md)：项目分层、请求链路、核心抽象、管理面与数据面关系。
- [存储与一致性](architecture/storage-and-consistency.md)：XlFs、MeansLogDb、写入事务、读路径、repair、GC 和当前边界。
- [S3 API 与兼容性](reference/s3-api-and-compatibility.md)：支持的 S3 操作、地址风格、认证、错误模型和兼容限制。
- [配置参考](reference/configuration.md)：`Means:*` 配置树、默认值、生产部署注意事项。
- [SDK 使用案例](sdk/sdk-usage-examples.md)：C#、Node.js、浏览器预签名上传、Multipart、Versioning、Lifecycle。
- [部署与可观测性](operations/deployment-observability.md)：本地、Docker、实验多节点、Prometheus、告警、生产检查。
- [开发、测试与路线图](development/testing-and-roadmap.md)：解决方案结构、构建测试命令、测试覆盖、已知限制和后续方向。

## 当前项目定位

Means 是一个可自托管的 S3-compatible 对象存储服务。当前实现重点是：

- 用 ASP.NET Core 承载 S3 数据面、Console 管理面、静态 Web 控制台、Prometheus 指标和后台任务。
- 默认使用 `Means.Infrastructure.XlFs` 存储后端，以多盘文件布局和 `MeansLogDb` 元数据索引提供本地持久化。
- 保留 `Means.Infrastructure.SqliteFs` 作为 legacy/test adapter，用于测试、兼容和迁移边界验证。
- 提供 C# SDK、浏览器安全 TypeScript SDK、Node.js TypeScript SDK 以及机器可读 SDK spec。
- 在单节点/多盘场景下提供较完整的 S3 基础能力，并为后续分布式 metadata、真实 EC、replication、IAM/STS、Object Lock 留出扩展面。

## 文档维护约定

- `docs/architecture` 解释内部设计和一致性边界。
- `docs/reference` 记录对外协议、配置、兼容性和错误模型。
- `docs/sdk` 面向应用开发者，示例优先。
- `docs/operations` 面向部署、监控、故障处理和生产准备。
- `docs/development` 面向贡献者、测试和路线图。

新增能力时优先更新对应分类文档，再同步 SDK README 或 docs-site 内容。
