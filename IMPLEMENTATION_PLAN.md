# Means 生产级分布式 S3 存储实现计划

## 状态说明

- `[x]` 已完成
- `[ ]` 未完成

状态维护规则：任务状态只能在实现完成且验证通过后，才允许从 `[ ]` 改为 `[x]`。

## 0. 当前已完成基线

- [x] ASP.NET Core 数据面基础服务
- [x] XlFs 自研存储后端默认接入
- [x] MeansLogDb WAL + snapshot 元数据索引
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

- [x] 定义 cluster/node/disk/pool 数据模型
- [x] 实现节点注册、心跳、上下线状态
- [x] 设计对象 placement 策略
- [x] 实现多副本写入
- [x] 实现多副本读取与副本选择
- [x] 实现副本修复与后台 repair queue
- [x] 设计纠删码 EC profile
- [x] 建立 XlFs quorum manifest 与读时坏 shard fallback
- [x] 实现 XlFs full-copy shard 修复与 scanner/heal 入队
- [ ] 实现 Reed-Solomon EC 写入、读取、重建
- [ ] 实现真实 rebalance 和数据迁移任务
- [x] 增加磁盘故障检测基础探测

## 2. 元数据与一致性

- [x] 实现 MeansLogDb append-only WAL、batch atomic commit、recovery replay
- [x] 实现 MeansLogDb snapshot/restore
- [x] 设计元数据事务边界
- [x] 定义 bucket/object/version/upload 元数据 schema
- [x] 实现强一致读写路径
- [x] 实现对象写入两阶段提交或等价机制
- [x] 实现幂等写入和请求重试保护
- [x] 元数据一致性自检与可修复问题入队
- [x] 存储孤儿文件 GC dry-run 与分批删除
- [x] 增加 metadata snapshot/backup/restore
- [x] 增加 schema migration 机制
- [x] 定义跨节点时钟与时间戳策略

## 3. S3 协议兼容补齐

- [x] Versioning：bucket versioning 状态
- [x] Versioning：object versionId 和 delete marker
- [x] Versioning：按 versionId GET/HEAD/DELETE
- [x] Versioning：ListObjectVersions
- [x] Lifecycle：expiration
- [x] Lifecycle：noncurrent version cleanup
- [x] Lifecycle：AbortIncompleteMultipartUpload 规则
- [x] UploadPartCopy
- [x] CopyObject metadata directive
- [x] Conditional headers：If-Match/If-None-Match
- [x] Object tagging
- [x] Bucket CORS
- [x] Bucket notification 预留接口
- [x] 更完整的 ListMultipartUploads/ListParts pagination
- [x] AWS CLI、boto3、aws-sdk-js、rclone、mc 兼容矩阵

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
- [x] API rate limit 与防滥用限制

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

- [x] Prometheus metrics
- [x] OpenTelemetry tracing
- [ ] structured logging
- [x] 后台任务调度框架
- [x] repair/rebalance/lifecycle/replication 任务统一管理
- [x] storage garbage collection 任务统一管理
- [x] Console 集群状态页面
- [x] Console 磁盘和节点健康页面
- [x] 管理 API 导出集群诊断信息
- [x] 告警规则文档

## 7. 性能与容量优化

- [x] GET 大对象零拷贝/低内存流式路径
- [x] PUT/Multipart Upload 并发限流
- [x] ListObjects 大 bucket 索引优化
- [x] 热点对象缓存策略
- [x] checksum 持久化与读时校验
- [x] 读时坏副本 fallback 与自动 repair 入队
- [x] 后台 scrub
- [x] 小对象合并或 packing 策略评估
- [x] 压缩策略从内存压缩改为流式压缩
- [x] 压测基准：吞吐、延迟、恢复时间

## 8. SDK、Console 与兼容测试

- [x] SDK 增加 Versioning API
- [x] SDK 增加 Lifecycle API
- [ ] SDK 增加 IAM/STS API
- [ ] SDK 增加 Object Lock API
- [x] Console 支持 version browser
- [x] Console 支持 lifecycle editor
- [x] Console 支持 cluster dashboard
- [x] Contract spec 覆盖新增 S3 API
- [x] golden fixtures 覆盖新增 XML 响应
- [x] 增加真实 S3 客户端兼容测试流水线

## 9. 里程碑验收

- [ ] M1：单机生产化 hardening
- [ ] M2：多副本分布式存储
- [x] M3：Versioning + Lifecycle
- [ ] M4：IAM/STS + SSE
- [ ] M5：Replication + Object Lock
- [ ] M6：EC + 自动 repair/rebalance
- [ ] M7：S3 兼容矩阵稳定通过
