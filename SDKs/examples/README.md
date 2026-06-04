# Means SDK Examples

This directory contains small, copyable SDK examples for common Means integration paths.

## Layout

- `csharp-basic/`: a runnable .NET console app that signs requests with `Means.Client`.
- `typescript/node-basic.mjs`: a trusted Node.js example using `@means/sdk-node`.
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

## Run the C# example

```bash
dotnet run --project SDKs/examples/csharp-basic/Means.SdkExamples.csproj
```

The sample creates a bucket if needed, uploads objects, reads metadata, creates presigned GET/PUT URLs, performs a presigned PUT through `HttpClient`, enables versioning, writes lifecycle rules, and performs a multipart upload.

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

## Browser upload example

`typescript/browser-presigned-upload.ts` intentionally does not contain an access key or secret key. It expects your own backend to issue short-lived presigned PUT/GET URLs, then uses the browser-safe `@means/sdk` helpers to upload and download objects.
