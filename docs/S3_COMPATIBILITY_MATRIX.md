# Means S3 协议兼容矩阵

## 当前覆盖

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| Bucket CRUD | 支持 | `PUT/HEAD/DELETE bucket` |
| Object CRUD | 支持 | `PUT/GET/HEAD/DELETE object` |
| ListObjectsV2 | 支持 | 支持 `prefix`、`delimiter`、`continuation-token`、`max-keys` |
| Range GET | 支持 | 返回 `206` 和 `Content-Range` |
| SigV4 header/query presign | 支持 | 签名覆盖 query subresource |
| Multipart Upload | 支持 | initiate/upload/list/complete/abort |
| ListMultipartUploads/ListParts pagination | 支持 | 支持 marker、max、delimiter/common prefixes |
| UploadPartCopy | 支持 | 支持完整对象 copy 和 `x-amz-copy-source-range` |
| CopyObject metadata directive | 支持 | 支持 `COPY`、`REPLACE` |
| Conditional headers | 支持 | `If-Match`、`If-None-Match` 覆盖 GET/HEAD/PUT |
| Bucket Versioning | 支持 | `Enabled`、`Suspended`、未配置状态 |
| Object versionId | 支持 | 对象版本使用 opaque object id 作为 version id |
| Delete marker | 支持 | 开启 versioning 后普通 DELETE 写 delete marker |
| GET/HEAD/DELETE by versionId | 支持 | 支持指定 `versionId` 访问和永久删除 |
| ListObjectVersions | 支持 | 支持 `prefix`、`delimiter`、`key-marker`、`version-id-marker`、`max-keys` |
| Object tagging | 支持 | 支持 current version 和指定 version tagging |
| Bucket Lifecycle | 支持 | 支持 expiration、noncurrent expiration、AbortIncompleteMultipartUpload |
| Bucket CORS | 支持 | 支持配置 CRUD 和 OPTIONS preflight |
| Bucket notification | 预留支持 | 支持配置持久化与读取，事件投递 worker 待后续实现 |
| Bucket Policy | 基础支持 | Allow/Deny，condition 尚待补齐 |

## 客户端兼容性目标

| 客户端 | 目标状态 | 验收范围 |
| --- | --- | --- |
| AWS CLI | 目标兼容 | bucket/object/multipart/versioning/tagging/cors/lifecycle 基础命令 |
| boto3 | 目标兼容 | 同步 client API smoke tests |
| aws-sdk-js v3 | 目标兼容 | S3Client command smoke tests |
| rclone | 目标兼容 | copy、sync、ls、cat、delete |
| MinIO Client (`mc`) | 目标兼容 | mb、cp、ls、cat、rm、stat |

## 明确限制

- 不模拟 AWS `CompleteMultipartUpload` 的 `200 OK` 内嵌错误边缘行为，失败直接返回 4xx XML 错误。
- Bucket notification 当前是控制面预留接口，尚不投递实际事件。
- Bucket Policy condition、IAM/STS、Object Lock 属于后续章节。
- Versioning 已支持对象版本写入、delete marker、指定 `versionId` 访问和 `ListObjectVersions` 基础分页。
