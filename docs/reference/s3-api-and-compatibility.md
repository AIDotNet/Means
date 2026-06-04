# S3 API 与兼容性

Means 实现 S3-compatible 数据面，目标是覆盖常用 bucket/object/multipart/versioning/lifecycle/tagging/cors/policy 能力，同时保持错误响应、XML 结构、SigV4 签名和分页语义尽量接近 S3。

## 地址风格

Means 支持三种入口：

| 风格 | 示例 | 说明 |
| --- | --- | --- |
| 同源 alias | `http://localhost:5178/s3/{bucket}/{key}` | 默认 `AliasPrefix=/s3`，适合单域名和 Console 浏览器上传 |
| Path-style | `https://api.means.local/{bucket}/{key}` | `Means:S3:ServiceHost` 指定 canonical host |
| Virtual-hosted-style | `https://{bucket}.means.local/{key}` | `Means:S3:DomainSuffix` 指定 bucket 子域后缀 |

本地反向代理或网关部署时，SDK 的 endpoint 必须指向真实 S3 数据面路径。例如服务在同域 `/s3` 下暴露时，endpoint 应配置为 `https://example.com/s3/`，而不是 Console 根路径。

## 认证模式

| 模式 | 适用场景 |
| --- | --- |
| SigV4 header | 服务端 SDK、AWS CLI、可信后端 |
| SigV4 query presign | 临时下载、临时上传、浏览器直传 |
| Anonymous policy | Bucket policy 显式允许的匿名读取或写入 |
| Console cookie | 仅 `/api/console` 管理面，不适用于 S3 数据面 |

SigV4 默认 region 为 `us-east-1`，service 为 `s3`。预签名 URL 最大有效期为 7 天。请求方法、path、query subresource 和 signed headers 必须与签名一致。

## 支持矩阵

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| ListBuckets | 支持 | `GET /`，需要签名 |
| CreateBucket | 支持 | `PUT /{bucket}` |
| HeadBucket | 支持 | `HEAD /{bucket}` |
| DeleteBucket | 支持 | 非空 bucket 返回 `BucketNotEmpty` |
| ListObjectsV2 | 支持 | `prefix`、`delimiter`、`continuation-token`、`max-keys` |
| PutObject | 支持 | metadata、content type、cache-control、content-disposition |
| GetObject | 支持 | Range、压缩、versionId |
| HeadObject | 支持 | metadata、versionId |
| DeleteObject | 支持 | versioning 下写 delete marker 或永久删除指定版本 |
| CopyObject | 支持 | `x-amz-copy-source`，`COPY`/`REPLACE` metadata directive |
| Conditional headers | 支持 | `If-Match`、`If-None-Match` 覆盖 GET/HEAD/PUT |
| Multipart Upload | 支持 | initiate/upload part/upload part copy/list/complete/abort |
| ListMultipartUploads | 支持 | marker、max、delimiter/common prefixes |
| ListParts | 支持 | part marker、max parts |
| Bucket Versioning | 支持 | `Enabled`、`Suspended`、未配置 |
| ListObjectVersions | 支持 | prefix、delimiter、key marker、version marker、max-keys |
| Object tagging | 支持 | current version 和指定 versionId |
| Bucket Lifecycle | 支持 | expiration、noncurrent expiration、abort incomplete multipart |
| Bucket CORS | 支持 | 配置 CRUD 与 OPTIONS preflight |
| Bucket notification | 预留 | 配置可持久化和读取，事件投递 worker 尚未实现 |
| Bucket Policy | 基础支持 | Allow/Deny、Principal、Action、Resource；condition 待补齐 |

## Multipart 规则

- `partNumber` 范围：`1..10000`。
- 非最终 part 至少 `5 MiB`。
- SDK 默认 part size：`16 MiB`。
- `CompleteMultipartUpload` 要求 part 编号升序且 ETag 匹配。
- Means 不模拟 AWS `CompleteMultipartUpload` 的 `200 OK` 内嵌错误边缘行为；失败直接返回 4xx XML 错误。

## 错误模型

S3 数据面统一返回 XML 错误：

```xml
<Error>
  <Code>NoSuchKey</Code>
  <Message>...</Message>
  <Resource>/bucket/key</Resource>
  <RequestId>...</RequestId>
</Error>
```

常见错误：

| Code | HTTP | 场景 |
| --- | --- | --- |
| `AccessDenied` | 403 | 未认证、policy 不允许、presign 过期或参数不完整 |
| `SignatureDoesNotMatch` | 403 | 方法、path、query、host、signed headers 或 secret 不匹配 |
| `NoSuchBucket` | 404 | bucket 不存在 |
| `NoSuchKey` | 404 | object 不存在 |
| `NoSuchVersion` | 404 | versionId 不存在 |
| `BucketAlreadyExists` | 409 | bucket 已存在 |
| `BucketNotEmpty` | 409 | 删除非空 bucket |
| `InvalidArgument` | 400 | bucket/key 命名、range、参数非法 |
| `InvalidPart` | 400 | multipart complete 的 part 或 ETag 不匹配 |
| `InvalidPartOrder` | 400 | multipart complete part 顺序错误 |
| `EntityTooSmall` | 400 | 非最终 multipart part 小于 5 MiB |
| `EntityTooLarge` | 413 | 请求体超过配置限制 |
| `SlowDown` | 503 | 上传并发或 API 固定窗口限流 |

Console API 错误为 JSON，不使用 XML。

## Bucket policy 支持范围

支持的 action 包括：

- `s3:ListBucket`
- `s3:GetObject`
- `s3:PutObject`
- `s3:DeleteObject`
- `s3:GetObjectTagging`
- `s3:PutObjectTagging`
- `s3:DeleteObjectTagging`
- `s3:GetBucketCORS`
- `s3:PutBucketCORS`
- `s3:GetBucketNotification`
- `s3:PutBucketNotification`
- `s3:AbortMultipartUpload`
- `s3:ListMultipartUploadParts`

当前支持 `Allow`/`Deny`、`Principal` 和 `Resource` 匹配；condition、IAM role、STS session、tenant namespace 是后续能力。

## 客户端兼容性目标

| 客户端 | 目标验证范围 |
| --- | --- |
| AWS CLI | bucket/object/multipart/versioning/tagging/cors/lifecycle 基础命令 |
| boto3 | 同步 S3 client API smoke tests |
| aws-sdk-js v3 | S3Client command smoke tests |
| rclone | copy、sync、ls、cat、delete |
| MinIO Client (`mc`) | mb、cp、ls、cat、rm、stat |

辅助脚本位于 `scripts/compatibility/run-s3-client-matrix.ps1`。
