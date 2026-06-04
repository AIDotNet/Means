# SDK 使用案例

Means 提供三类 SDK：

- `Means.Client`：C# SDK，适合 .NET 服务端、后台任务和 CLI。
- `@means/sdk`：浏览器安全 TypeScript SDK，不包含 SecretKey 签名能力。
- `@means/sdk-node`：Node.js 扩展，适合可信服务端生成签名请求和 presigned URL。

可运行示例位于 `SDKs/examples`：

- `SDKs/examples/csharp-basic`
- `SDKs/examples/typescript/node-basic.mjs`
- `SDKs/examples/typescript/browser-presigned-upload.ts`

## 端点选择

SDK endpoint 必须指向 S3 数据面：

| 部署方式 | endpoint 示例 |
| --- | --- |
| 本地同源 alias | `http://localhost:5178/s3/` |
| 独立 path-style host | `https://api.means.local/` |
| 网关 path prefix | `https://storage.example.com/s3/` |
| virtual-hosted-style | `https://means.local/` 并设置 virtual hosted suffix |

如果 endpoint 指向 Console 根路径，而真实 S3 数据面在 `/s3`，预签名 PUT/GET 会打到错误路由。

## C#：基础对象操作

```csharp
using Means;

using var client = new MeansClient(new MeansClientOptions
{
    Endpoint = new Uri("http://localhost:5178/s3/"),
    Credentials = new MeansCredentials("meansadmin", "meansadminsecret"),
    ForcePathStyle = true
});

await client.CreateBucketAsync("photos");

await using var body = File.OpenRead("image.jpg");
await client.PutObjectAsync(
    "photos",
    "2026/image.jpg",
    body,
    contentType: "image/jpeg",
    metadata: new Dictionary<string, string> { ["origin"] = "camera" });

await using var downloaded = await client.GetObjectAsync("photos", "2026/image.jpg");
await using var output = File.Create("downloaded.jpg");
await downloaded.Content.CopyToAsync(output);
```

## C#：预签名上传和下载

```csharp
var putUrl = client.CreatePresignedPutUrl(
    "photos",
    "incoming/browser-upload.jpg",
    TimeSpan.FromMinutes(10));

var getUrl = client.CreatePresignedGetUrl(
    "photos",
    "incoming/browser-upload.jpg",
    TimeSpan.FromMinutes(10));

Console.WriteLine(putUrl.Url);
Console.WriteLine(getUrl.Url);
```

预签名 URL 最大有效期为 7 天。PUT URL 必须用 PUT 方法调用，GET URL 必须用 GET 方法调用。

## C#：Multipart 上传

```csharp
await using var largeFile = File.OpenRead("video.mp4");
var completed = await client.UploadObjectMultipartAsync(
    "photos",
    "videos/video.mp4",
    largeFile,
    contentType: "video/mp4");

Console.WriteLine(completed.ETag);
```

高阶 helper 默认使用 16 MiB part，并在失败时自动 abort。需要自定义调度、断点续传或 presigned part URL 时，使用 `InitiateMultipartUploadAsync`、`UploadPartAsync`、`CompleteMultipartUploadAsync` 等低阶 API。

## Node.js：可信服务端签名

```ts
import { MeansNodeClient } from "@means/sdk-node";

const client = new MeansNodeClient({
  endpoint: "http://localhost:5178/s3/",
  addressingStyle: "path",
  credentials: {
    accessKey: process.env.MEANS_ACCESS_KEY!,
    secretKey: process.env.MEANS_SECRET_KEY!,
  },
});

await client.putObject({
  bucket: "assets",
  key: "app/main.js",
  body: "console.log('means')",
  contentType: "application/javascript",
  cacheControl: "public, max-age=31536000, immutable",
});

const url = client.createPresignedGetUrl({
  bucket: "assets",
  key: "app/main.js",
  expiresIn: 900,
});
```

Node SDK 可以直接签名请求，也可以为浏览器端生成短期 presigned URL。

## 浏览器：只执行 presigned URL

浏览器端不能持有 SecretKey。推荐流程：

1. 浏览器向业务后端请求上传授权。
2. 业务后端使用 C# SDK 或 Node SDK 生成 presigned PUT URL。
3. 浏览器用 `@means/sdk` 执行 PUT。
4. 业务后端保存 object key 或返回 presigned GET URL。

```ts
import { putObjectToUrl } from "@means/sdk";

await putObjectToUrl(presignedPutUrl, file, {
  contentType: file.type || "application/octet-stream",
});
```

## Versioning 与 Lifecycle

```ts
await client.setBucketVersioning({ bucket: "assets", status: "Enabled" });

const versions = await client.listObjectVersions({
  bucket: "assets",
  prefix: "app/",
});

await client.putBucketLifecycle({
  bucket: "assets",
  configuration: {
    rules: [
      {
        id: "expire-temp",
        status: "Enabled",
        prefix: "tmp/",
        expirationDays: 7,
        noncurrentVersionExpirationDays: 3,
        abortIncompleteMultipartUploadDays: 1,
      },
    ],
  },
});
```

Lifecycle day 值必须是正整数。删除所有规则使用 `deleteBucketLifecycle` / `DeleteBucketLifecycleAsync`。

## 常见 SDK 排查

| 问题 | 检查项 |
| --- | --- |
| `SignatureDoesNotMatch` | endpoint host/path、请求方法、query、region、service、系统时间 |
| presigned PUT 返回 404 | endpoint 是否包含真实 S3 数据面路径，例如 `/s3/` |
| 浏览器上传 403 | presigned URL 是否过期，method 是否为 PUT，bucket policy 是否允许 |
| CORS preflight 失败 | bucket CORS 是否包含来源、方法、header |
| Multipart complete 失败 | part 顺序、ETag、非最终 part 是否至少 5 MiB |
