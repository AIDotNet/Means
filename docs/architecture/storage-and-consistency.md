# 存储与一致性

Means 当前默认存储后端是 `Means.Infrastructure.XlFs`。它把对象字节写入多盘文件布局，把 bucket/object/version/upload/access key/policy/settings/audit/task/EC profile 等元数据写入自研 `MeansLogDb`。旧的外部数据库后端已移除，运行时只使用自研 MeansLogDb。

## XlFs 数据布局

XlFs 的核心目标是让对象 key 与磁盘文件名解耦。对象真实数据使用 opaque object id、manifest 和 shard/replica 路径定位，而不是直接使用用户提供的 object key。

主要组成：

- 磁盘根目录：由 `Means:Storage:Disks` 或 `ObjectsPath` 决定。
- 格式文件：每块盘写入 `.means.sys/format.json`，用于固定 deployment/set/disk 格式。
- 临时文件：上传请求先落到 `.means.sys/tmp`。
- 对象 shard/replica：按 bucket hash、object id 和 set index 写入对象数据。
- `xl.meta` manifest：记录对象、part、shard、checksum、大小、content type 等定位信息。
- `MeansLogDb`：append-only WAL + snapshot 的元数据索引。

## 元数据职责

`MeansLogDb` 管理命名空间元数据：

- bucket 记录。
- current object pointer。
- object version 记录。
- multipart upload 与 part 记录。
- access key、bucket policy、bucket settings。
- audit、request metrics、system settings。
- cluster topology、disk/node heartbeat、EC profile。
- background task snapshot/history。

对象字节、multipart part 文件、replica/shard 文件和 manifest 属于数据层。元数据只保存 opaque id、路径、校验和、版本和状态。

## 写入事务边界

普通 `PutObject` 的可见性由最终元数据 commit 控制：

1. 请求体流式写入临时文件。
2. 计算 hash、长度和基础 metadata。
3. 按 XlFs 布局复制到参与磁盘或对象目录。
4. 写入对象 manifest。
5. 用一个 `MeansLogDb` batch 写入 object version 与 current object pointer。
6. commit 成功后对象才对 `GET/HEAD/LIST` 可见。

任一步失败时，已写入但未提交的 temp/replica/shard 文件会 best-effort 删除。覆盖写入时，旧对象文件只在新元数据提交成功后才进入清理流程，避免读路径出现不可读窗口。

## 读路径

读路径先查 `MeansLogDb` 的 current object pointer 或指定 version，再解析 manifest 并选择可读 replica/shard：

- `GET` 返回对象流，优先使用可直接发送的文件路径。
- `HEAD` 返回 metadata 和对象头。
- `LIST` 只展示已提交的 current object，不展示未完成 multipart 或未提交对象。
- Range GET 支持 `206 PartialContent`；非法 range 返回 `416 InvalidRange` 和 `Content-Range: bytes */{length}`。
- 压缩协商支持 `br` 和 `gzip`，Range 请求禁用压缩。

默认 `VerifyChecksumOnRead=false`，避免大对象 GET 产生额外全量 hash I/O。需要强读时可开启读时 checksum；后台 scrub 仍可周期性校验并入队 repair。

## Multipart 一致性

Multipart upload 的状态由 upload id 和 part 记录管理：

- `InitiateMultipartUpload` 创建 upload metadata。
- `UploadPart` 写入 part 文件和 part metadata。
- `UploadPartCopy` 允许从已有对象复制 part。
- `ListParts` 和 `ListMultipartUploads` 支持分页。
- `CompleteMultipartUpload` 校验 part 顺序、ETag 和非最终 part 最小 5 MiB，再提交最终对象。
- `AbortMultipartUpload` 删除 upload metadata，并 best-effort 清理 part。

完成后的 multipart ETag 使用 S3 风格：`md5(concat(part-md5-bytes))-part-count`。

## Versioning 与 delete marker

Versioning 支持 `Enabled`、`Suspended` 和未配置状态。开启 versioning 后：

- 每次写入创建新的 object version。
- 普通 DELETE 写入 delete marker。
- 指定 `versionId` 的 GET/HEAD/DELETE 可访问或永久删除特定版本。
- `ListObjectVersions` 支持 prefix、delimiter、key marker、version marker 和 max-keys。

Means 使用 opaque object id 作为 version id，这让物理数据布局不暴露给用户 object key。

## Lifecycle、GC 与 repair

已实现的维护能力：

- Lifecycle：支持 current expiration、noncurrent version expiration、abort incomplete multipart upload。
- Multipart cleanup：按年龄清理未完成 upload。
- Object scrub：校验对象副本，发现缺失或损坏时入队 repair。
- Replica repair：修复缺失/损坏副本并跟踪最大重试。
- Storage GC：扫描受控目录，按元数据引用集和年龄保护清理 orphan/temp 文件。
- Metadata consistency：检查 pending commit、orphan replica record、manifest 缺失等问题。

后台任务统一暴露给 Console 和 `/metrics`，便于运维查看任务成功、失败、耗时和当前状态。

## 热点对象与性能策略

当前性能设计重点：

- 大对象 GET 使用 `SendFileAsync` 或低内存文件流路径。
- 压缩为流式 gzip/br，避免完整缓存在内存。
- ListObjects/ListVersions/ListMultipartUploads 使用有序前缀扫描，避免大 bucket 全表扫描。
- PUT 与 UploadPart 受 `MaxConcurrentUploadRequests` 全局并发限制，超过返回 S3 XML `SlowDown`。
- 热点小对象缓存以 object id 为 key，默认总预算 64 MiB，单对象上限 1 MiB。

暂不实现 small object packing，因为 pack index、compaction、碎片回收和并发维护复杂度高。当前阶段优先使用热点缓存和前缀索引优化小对象场景。

## 当前边界

- XlFs 普通 `PutObject` 已支持本地对象级 `reed-solomon-v1` data/parity shards，并可在缺失 data shard 时从剩余 shard 重建读取；multipart EC、跨节点 EC/RPC、生产级 EC repair/rebalance 仍是后续项。
- 多节点 compose 不共享同一个分布式 metadata 命名空间，不应作为生产数据面负载均衡。
- Replication worker、Bucket notification 事件投递、Object Lock、IAM/STS、SSE 仍是规划能力。
