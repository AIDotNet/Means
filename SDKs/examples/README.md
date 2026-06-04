# Means SDK Examples

This directory contains small, copyable SDK examples for common Means integration paths.

## Layout

- `csharp-basic/`: a runnable .NET console app that signs requests with `Means.Client`.
- `csharp-aws-sdk/`: a runnable .NET console app using the official `AWSSDK.S3` package.
- `typescript/node-basic.mjs`: a trusted Node.js example using `@means/sdk-node`.
- `typescript/aws-sdk-v3.mjs`: a trusted Node.js example using the official AWS SDK for JavaScript v3.
- `typescript/s3-compatibility-check.mjs`: a non-destructive AWS SDK v3 preflight for endpoint/signing configuration.
- `typescript/browser-presigned-upload.ts`: a browser-side example using `@means/sdk` with presigned URLs.

## Local defaults

The examples assume a local Means service with the same-origin S3 alias:

```text
MEANS_ENDPOINT=http://localhost:5178/s3/
MEANS_ACCESS_KEY=meansadmin
MEANS_SECRET_KEY=meansadminsecret
MEANS_BUCKET=sdk-examples
```

For a production deployment, point `MEANS_ENDPOINT` at the S3 data-plane endpoint, not the console API endpoint. If your deployment exposes S3 under `/s3`, include that path in the endpoint.

## Check S3 compatibility first

Before running examples that write objects, validate the endpoint, credentials, region, and path-style configuration:

```bash
cd SDKs/examples/typescript
npm install
npm run check:s3
```

The check uses the official AWS SDK for JavaScript v3 with `forcePathStyle: true`. It runs `ListBuckets` and, when `MEANS_BUCKET` is set, `HeadBucket`. This is useful for multi-node or load-balanced deployments because it catches the most common issues early: pointing at the console API instead of the S3 data plane, forgetting the `/s3/` path prefix, mismatched signing region, or using credentials from the wrong environment.

The C# AWS SDK example has the same read-only preflight mode:

```bash
dotnet run --project SDKs/examples/csharp-aws-sdk/Means.AwsSdkExamples.csproj -- check
```

## Run the C# example

```bash
dotnet run --project SDKs/examples/csharp-basic/Means.SdkExamples.csproj
```

The sample creates a bucket if needed, uploads objects, reads metadata, creates presigned GET/PUT URLs, performs a presigned PUT through `HttpClient`, enables versioning, writes lifecycle rules, and performs a multipart upload.

## Run the C# AWS SDK example

```bash
dotnet run --project SDKs/examples/csharp-aws-sdk/Means.AwsSdkExamples.csproj
```

The sample uses `AWSSDK.S3` with `ServiceURL`, `AuthenticationRegion`, and `ForcePathStyle` configured for a Means S3 endpoint. It creates a bucket if needed, uploads an object, lists objects, downloads the object, and prints a presigned GET URL.

## Run the Node.js example

Build the TypeScript packages first:

```bash
cd SDKs/typescript
npm install
npm run build
```

Then install the example-local package links and run the Node sample:

```bash
cd SDKs/examples/typescript
npm install
npm run node-basic
```

## Run the Node.js AWS SDK example

```bash
cd SDKs/examples/typescript
npm install
npm run aws-sdk-v3
```

The sample uses `@aws-sdk/client-s3` and `@aws-sdk/s3-request-presigner` with a custom endpoint and `forcePathStyle: true`. It creates a bucket if needed, uploads an object, lists objects, downloads the object, and prints a presigned GET URL.

## Browser upload example

`typescript/browser-presigned-upload.ts` intentionally does not contain an access key or secret key. It expects your own backend to issue short-lived presigned PUT/GET URLs, then uses the browser-safe `@means/sdk` helpers to upload and download objects.
