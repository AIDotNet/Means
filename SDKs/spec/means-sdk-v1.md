# Means SDK Contract v1

This document is the human-readable contract for official Means SDKs. The machine-readable companion is `means-sdk-v1.yaml`; generated SDKs and hand-written SDKs must treat the YAML plus fixtures as the source of truth.

## Endpoint Styles

Means SDKs must support both address styles:

- Path-style: `https://api.means.local/{bucket}/{key}`
- Virtual-hosted-style: `https://{bucket}.means.local/{key}`

The default SDK mode is path-style for local development and virtual-hosted-style for production.

## Authentication

Node and server-side SDKs support SigV4 header signing and query presigning. Browser SDKs must not expose `AccessKey/SecretKey`; browser callers use anonymous access or presigned URLs.

Default scope:

- Algorithm: `AWS4-HMAC-SHA256`
- Service: `s3`
- Region: `us-east-1`
- Payload hash: `UNSIGNED-PAYLOAD` for SDK-generated requests
- Presigned URL maximum expiration: `604800` seconds, which is seven days

## Naming Rules

Bucket names use the v1 DNS-compatible subset:

- length is 3 to 63 characters
- lower-case letters, digits, hyphen, and dot only
- first and last characters must be letters or digits
- consecutive dots, dot-hyphen, hyphen-dot, and IPv4-address-shaped names are rejected

Object keys use the v1 storage-safe subset:

- non-empty UTF-8 string
- maximum encoded length is 1,024 bytes
- control characters are rejected

## Operations

All SDKs expose the same logical operations:

- `listBuckets`
- `createBucket`
- `headBucket`
- `deleteBucket`
- `listObjects`
- `putObject`
- `getObject`
- `headObject`
- `deleteObject`
- `copyObject`
- `initiateMultipartUpload`
- `uploadPart`
- `completeMultipartUpload`
- `abortMultipartUpload`
- `listParts`
- `listMultipartUploads`
- `createPresignedGetUrl`
- `createPresignedPutUrl`
- `createPresignedUploadPartUrl`

Language-specific casing may follow the host language: C# uses PascalCase async methods, TypeScript uses camelCase methods.

## Multipart Upload

SDKs support client-side multipart upload with these constraints:

- `partNumber` is `1..10000`
- non-final parts must be at least `5 MiB`
- SDK high-level helpers default to `16 MiB` parts
- final object ETag is `md5(concat(part-md5-bytes))-part-count`
- incomplete uploads do not appear in normal object listing
- `UploadPartCopy` is not part of v1

## Error Model

S3 XML errors are mapped into a typed SDK error with:

- `code`
- `message`
- `statusCode`
- `requestId`
- `resource`

SDKs must preserve the original HTTP status and S3-style error code.

## Compression and Range

SDKs do not decompress manually. They preserve response headers and let the runtime transport handle content decoding when applicable.

When a caller sends a Range request, SDKs must not add `Accept-Encoding` for automatic compression. Means serves Range responses from the original uncompressed object representation.

Invalid or unsatisfiable Range requests return `416 InvalidRange` with `Content-Range: bytes */{object-length}`.

## Browser Rules

The browser-safe TypeScript package supports:

- anonymous `GET`, `HEAD`, and `ListObjectsV2` when bucket policy allows it
- `GET` and `PUT` using presigned URLs
- no direct SigV4 signing with secret keys

The Node package supports the full operation set.
