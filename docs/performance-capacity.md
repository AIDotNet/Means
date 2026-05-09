# 性能与容量优化说明

## 已落地能力

- 大对象 GET 使用 `SendFileAsync` 优先走低内存文件发送路径；Range GET 在文件流可用时同样直接按偏移发送。
- 压缩响应改为流式 gzip/br，不再把完整压缩结果缓存在内存中。
- ListObjects/ListObjectVersions/ListMultipartUploads 的 prefix 查询使用有序范围扫描，避免 `LIKE prefix%` 造成的大 bucket 全表扫描。
- PUT 和 Multipart Upload 受全局并发限流保护，超过限制返回 S3 `SlowDown`。
- 对象副本持久化 SHA-256 checksum，后台 scrub 定期校验并把缺失或损坏副本加入 repair queue。
- `VerifyChecksumOnRead` 可按需开启读路径 checksum 校验；默认关闭以避免大对象 GET 额外全量哈希开销。
- 热点小对象缓存使用对象 ID 作为 key，默认 64 MiB 总预算、1 MiB 单对象上限；只缓存小对象，不影响大对象零拷贝路径。

## Small Object Packing 评估结论

当前实现暂不把小对象合并写入 pack file，原因：

- 现有对象 ID 到独立文件的布局让 delete/versioning/repair/rebalance 都保持简单，故障域清晰。
- 小对象 packing 会引入 pack 索引、空间回收、碎片整理和并发 compaction，复杂度明显高于当前单机/多副本阶段收益。
- 对当前阶段，热点小对象读缓存和 prefix 索引优化能覆盖主要 Console/API 读放大问题。

后续达到大量小于 64 KiB 对象、inode 压力或目录元数据成为瓶颈时，再实现 append-only pack segment：对象元数据记录 `pack_id/offset/length/checksum`，后台 compaction 只处理无当前版本引用的 segment。

## 压测基准

`scripts/benchmarks/means-s3-benchmark.ps1` 提供 AWS CLI 驱动的基础吞吐和延迟基准，覆盖 PUT/GET/List/Delete。推荐在独立磁盘和固定并发下记录：

- PUT 吞吐与 P95/P99 延迟
- GET 吞吐与 P95/P99 延迟
- Multipart 大对象吞吐
- ListObjects 大 bucket 延迟
- repair/rebalance/scrub 运行时的前台请求影响
