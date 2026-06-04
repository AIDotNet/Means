# Means TypeScript SDKs

This workspace contains the publishable TypeScript packages for Means object storage.

- `@means/sdk` is the shared browser-safe package. It builds S3-style Means URLs, executes anonymous requests, executes presigned GET/PUT URLs, parses S3 XML responses, and never contains AccessKey/SecretKey signing code.
- `@means/sdk-node` is the Node extension. It depends on `@means/sdk`, adds AccessKey/SecretKey credentials, SigV4 request signing, and presigned URL generation.

Both packages share the low-level S3-compatible API surface for bucket/object CRUD, multipart upload, `UploadPartCopy`, bucket versioning, versioned object reads/deletes, object version listing, lifecycle configuration, object tagging, bucket CORS, and bucket notification. Browser callers still use anonymous policy or presigned URLs for write access.

## Examples

Runnable and copyable examples live in `../examples`:

- `../examples/typescript/node-basic.mjs` shows trusted Node.js signing, object CRUD, presigned URLs, versioning, and lifecycle.
- `../examples/typescript/browser-presigned-upload.ts` shows browser upload/download through presigned URLs without exposing secret keys.

## Build

```bash
cd SDKs/typescript
npm install
npm run build
```

Each package emits:

- `dist/esm/index.mjs`
- `dist/cjs/index.js`
- `dist/types/index.d.ts`
