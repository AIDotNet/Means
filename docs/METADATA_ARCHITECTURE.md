# Means 元数据架构与一致性边界

## 目标

当前默认实现是 `Means.Infrastructure.XlFs`：对象字节写入多盘 XlFs 布局，bucket/object/version/upload/access key/policy/settings/audit/task 等命名空间元数据写入自研 `MeansLogDb`。SQLite adapter 仍保留为 legacy/test 后端，但不再是默认运行路径。S3 路径依赖 `IObjectStore`，集群控制面依赖 `IClusterStore`，元数据维护能力依赖 `IMetadataMaintenanceStore`；后续替换为多节点 Raft/consensus 元数据服务时，应保持这些端口语义不变。

## 元数据职责

- Bucket、Object、ObjectVersion、MultipartUpload、Part、AccessKey、Policy、Settings、Audit、Task、EC Profile 都属于 `MeansLogDb` 命名空间元数据层。
- 对象字节、multipart part 文件、replica/shard 文件和 `xl.meta` manifest 属于 XlFs 数据层；元数据只保存 opaque id、路径、校验和与状态。
- 普通对象 listing 只读取 `objects` 当前可见表，未完成 multipart 和未提交对象不会出现在 listing。

## 写入事务边界

- 请求体先流式落临时文件，再按 XlFs quorum 写入参与磁盘，随后写入 `xl.meta` manifest。
- 可见性只由最终 `MeansLogDb` batch commit 控制；未提交对象即使 shard 文件已存在，也不会出现在 HEAD/List 中。
- 最终提交顺序为：写入 shard、写入 `xl.meta`、以 LogDb batch 写入 object version 与 current object pointer。
- 任一事务失败时，已写入但未提交的 replica/shard/temp 文件会 best-effort 删除。
- 旧对象 replica/shard 只在新元数据提交成功后删除，避免覆盖对象时出现不可读窗口。

## 强一致读写

- 当前对象读、HEAD、LIST 只读取已提交的 `MeansLogDb` current object pointer。
- 写入提交前即使数据文件已存在，也不会通过 S3 API 可见。
- 覆盖写使用同一个 LogDb batch 替换 current pointer，读路径不会看到半提交状态。

## 幂等与重试

- legacy SQLite 后端仍支持 `x-means-idempotency-key` 到 `idempotency_records` 的幂等映射。
- XlFs 首期通过对象级 atomic commit 和 opaque versionId 避免半提交可见；完整幂等 key namespace 后续仍需补齐。
- 同一幂等 key、operation、bucket、key、request hash 会返回原始 object version 结果。
- 同一幂等 key 对应不同请求会返回 `InvalidRequest` 409，避免客户端重试误覆盖。

## 时间戳策略

- XlFs 首期使用 UTC wall clock 记录对象、upload、audit 时间。
- 多节点元数据服务替换 `MeansLogDb` 时必须提供单调提交时间语义，避免 version/list 排序在时钟回拨时退化。

## 迁移、快照与恢复

- XlFs 磁盘格式由每块盘的 `.means.sys/format.json` 固定，当前格式版本为 `1`。
- `CreateMetadataSnapshotAsync` 导出 `MeansLogDb` 当前索引快照。
- `RestoreMetadataSnapshotAsync` 用快照替换当前 LogDb 索引并重建 WAL。
- legacy SQLite 后端仍使用 SQLite online backup/restore。

## 后续替换要求

- 新元数据后端必须支持条件写、事务提交、前缀 listing、幂等记录唯一约束、单调 commit timestamp。
- Object data path 不应依赖真实 object key 文件名，仍以 opaque object id、replica manifest 和 EC manifest 定位数据。
- 后端切换不应改变 S3 API、Console API 或 SDK 语义。
