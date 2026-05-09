# @means/sdk-node

Node.js extension for the Means TypeScript SDK.

Use this package on trusted servers and CLIs. It accepts AccessKey/SecretKey credentials, signs requests with Signature Version 4, and creates presigned GET/PUT URLs for untrusted browser clients.

## Install

```bash
npm install @means/sdk-node
```

## Signed operations

```ts
import { MeansNodeClient } from "@means/sdk-node";

const client = new MeansNodeClient({
  endpoint: "https://api.means.local",
  credentials: {
    accessKey: process.env.MEANS_ACCESS_KEY!,
    secretKey: process.env.MEANS_SECRET_KEY!
  }
});

await client.createBucket({ bucket: "assets" });

await client.putObject({
  bucket: "assets",
  key: "app/main.js",
  body: "console.log('means')",
  contentType: "application/javascript",
  cacheControl: "public, max-age=31536000, immutable"
});

const objects = await client.listObjects({
  bucket: "assets",
  prefix: "app/"
});
```

## Presigned URLs

```ts
const getUrl = client.createPresignedGetUrl({
  bucket: "assets",
  key: "app/main.js",
  expiresIn: 900
});

const putUrl = client.createPresignedPutUrl({
  bucket: "assets",
  key: "uploads/new-file.bin",
  expiresIn: 900
});
```

Multipart part URLs include `partNumber` and `uploadId` in the SigV4 signature:

```ts
const partUrl = client.createPresignedUploadPartUrl({
  bucket: "assets",
  key: "uploads/large.bin",
  uploadId,
  partNumber: 1,
  expiresIn: 900
});
```

## Multipart uploads

```ts
await client.uploadFileMultipart({
  bucket: "assets",
  key: "videos/demo.mp4",
  filePath: "./demo.mp4",
  contentType: "video/mp4"
});
```

`uploadFileMultipart` streams the file in 16 MiB parts by default and aborts the upload on failure. `uploadBufferMultipart` is available for `Buffer`/`Uint8Array` bodies.
