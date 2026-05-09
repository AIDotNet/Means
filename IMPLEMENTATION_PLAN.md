# Means 生产级分布式 S3 存储实现计划

## 状态说明

- `[x]` 已完成
- `[ ]` 未完成

状态维护规则：任务状态只能在实现完成且验证通过后，才允许从 `[ ]` 改为 `[x]`。

## 0. 当前已完成基线

- [x] ASP.NET Core 数据面基础服务
- [x] SQLite + filesystem 单机对象存储
- [x] Bucket/Object 基础 CRUD
- [x] ListObjectsV2
- [x] Range GET
- [x] SigV4 header/query presign
- [x] Bucket Policy 基础 Allow/Deny
- [x] Multipart Upload 基础全链路
- [x] Console API 与 React 控制台
- [x] C# SDK、TypeScript SDK、Node SDK 基线
- [x] Multipart 未完成上传后台清理

## 1. 分布式存储内核

- [ ] 定义 cluster/node/disk/pool 数据模型
- [ ] 实现节点注册、心跳、上下线状态
- [ ] 设计对象 placement 策略
- [ ] 实现多副本写入
- [ ] 实现多副本读取与副本选择
- [ ] 实现副本修复与后台 repair queue
- [ ] 设计纠删码 EC profile
- [ ] 实现 EC 写入、读取、重建
- [ ] 实现 rebalance 和数据迁移任务
- [ ] 增加磁盘故障检测与自动隔离

## 2. 元数据与一致性

- [ ] 替换单机 SQLite 元数据瓶颈的目标架构
- [ ] 设计元数据事务边界
- [ ] 定义 bucket/object/version/upload 元数据 schema
- [ ] 实现强一致读写路径
- [ ] 实现对象写入两阶段提交或等价机制
- [ ] 实现幂等写入和请求重试保护
- [ ] 增加 metadata snapshot/backup/restore
- [ ] 增加 schema migration 机制
- [ ] 定义跨节点时钟与时间戳策略

## 3. S3 协议兼容补齐

- [ ] Versioning：bucket versioning 状态
- [ ] Versioning：object versionId 和 delete marker
- [ ] Versioning：按 versionId GET/HEAD/DELETE
- [ ] Lifecycle：expiration
- [ ] Lifecycle：noncurrent version cleanup
- [ ] Lifecycle：AbortIncompleteMultipartUpload 规则
- [ ] UploadPartCopy
- [ ] CopyObject metadata directive
- [ ] Conditional headers：If-Match/If-None-Match
- [ ] Object tagging
- [ ] Bucket CORS
- [ ] Bucket notification 预留接口
- [ ] 更完整的 ListMultipartUploads/ListParts pagination
- [ ] AWS CLI、boto3、aws-sdk-js、rclone、mc 兼容矩阵

## 4. 安全、租户与权限

- [ ] IAM 用户、角色、策略模型
- [ ] STS 临时凭证
- [ ] Policy condition 支持
- [ ] per-tenant namespace 和资源隔离
- [ ] AccessKey rotation
- [ ] SSE-S3
- [ ] SSE-KMS 抽象
- [ ] 审计日志不可篡改存储
- [ ] 管理面 RBAC
- [ ] API rate limit 与防滥用限制

## 5. 生命周期、合规与数据治理

- [ ] Object Lock 基础模型
- [ ] Retention policy
- [ ] Legal hold
- [ ] Bucket quota
- [ ] User/tenant quota
- [ ] Storage class 抽象
- [ ] Replication 规则模型
- [ ] 跨 bucket/跨 cluster replication worker
- [ ] Replication 状态追踪和失败重试

## 6. 运维、可观测性与后台任务

- [ ] Prometheus metrics
- [ ] OpenTelemetry tracing
- [ ] structured logging
- [ ] 后台任务调度框架
- [ ] repair/rebalance/lifecycle/replication 任务统一管理
- [ ] Console 集群状态页面
- [ ] Console 磁盘和节点健康页面
- [ ] 管理 API 导出集群诊断信息
- [ ] 告警规则文档

## 7. 性能与容量优化

- [ ] GET 大对象零拷贝/低内存流式路径
- [ ] PUT/Multipart Upload 并发限流
- [ ] ListObjects 大 bucket 索引优化
- [ ] 热点对象缓存策略
- [ ] checksum 持久化与读时校验
- [ ] 后台 scrub
- [ ] 小对象合并或 packing 策略评估
- [ ] 压缩策略从内存压缩改为流式压缩
- [ ] 压测基准：吞吐、延迟、恢复时间

## 8. SDK、Console 与兼容测试

- [ ] SDK 增加 Versioning API
- [ ] SDK 增加 Lifecycle API
- [ ] SDK 增加 IAM/STS API
- [ ] SDK 增加 Object Lock API
- [ ] Console 支持 version browser
- [ ] Console 支持 lifecycle editor
- [ ] Console 支持 cluster dashboard
- [ ] Contract spec 覆盖新增 S3 API
- [ ] golden fixtures 覆盖新增 XML 响应
- [ ] 增加真实 S3 客户端兼容测试流水线

## 9. 里程碑验收

- [ ] M1：单机生产化 hardening
- [ ] M2：多副本分布式存储
- [ ] M3：Versioning + Lifecycle
- [ ] M4：IAM/STS + SSE
- [ ] M5：Replication + Object Lock
- [ ] M6：EC + 自动 repair/rebalance
- [ ] M7：S3 兼容矩阵稳定通过
