# Means C# SDK

Standalone C# SDK for the Means S3-compatible object storage API. The package targets `net8.0` and `netstandard2.1`, uses `HttpClient`, signs requests with SigV4, parses S3-style XML responses, and throws `MeansError` for S3-style XML errors.

## Install from source

```bash
dotnet build SDKs/csharp/Means.Client.csproj -c Release
# or
dotnet pack SDKs/csharp/Means.Client.csproj -c Release
```

`Release` 构建或打包会在 `SDKs/csharp/bin/Release` 生成 `Means.Client.0.1.1.nupkg` 和 `Means.Client.0.1.1.snupkg`。
Reference the generated `Means.Client` package or project from your application.

## Basic usage

For a runnable end-to-end sample, see `../examples/csharp-basic`. It covers bucket creation, object upload/download, presigned GET/PUT URLs, versioning, lifecycle, and multipart upload.

```csharp
using Means;

var client = new MeansClient(new MeansClientOptions
{
    Endpoint = new Uri("http://localhost:5000"),
    Credentials = new MeansCredentials("access-key", "secret-key"),
    Region = "us-east-1",
    ForcePathStyle = true
});

await client.CreateBucketAsync("photos");

await using var upload = File.OpenRead("image.jpg");
await client.PutObjectAsync(
    "photos",
    "2026/image.jpg",
    upload,
    contentType: "image/jpeg",
    metadata: new Dictionary<string, string> { ["origin"] = "camera" });

var objects = await client.ListObjectsAsync("photos", prefix: "2026/");

await using var download = await client.GetObjectAsync("photos", "2026/image.jpg");
await using var file = File.Create("downloaded.jpg");
await download.Content.CopyToAsync(file);
```

## API surface

- `MeansClient`
- `MeansClientOptions`
- `MeansCredentials`
- `BucketSummary`
- `ObjectSummary`
- `BucketVersioningResult`
- `ListObjectVersionsResult`
- `BucketLifecycleConfiguration`
- `ObjectHeadResult`
- `PutObjectResult`
- `DeleteObjectResult`
- `CopyObjectResult`
- `CopyObjectOptions`
- `ObjectTaggingResult`
- `BucketXmlConfigurationResult`
- `MultipartUploadResult`
- `UploadPartResult`
- `CopyPartResult`
- `CompleteMultipartUploadResult`
- `ListPartsResult`
- `ListMultipartUploadsResult`
- `ListMultipartUploadsOptions`
- `PresignedRequest`
- `MeansError`

`MeansClient` exposes PascalCase async methods for S3-compatible operations:

- `ListBucketsAsync`
- `CreateBucketAsync`
- `HeadBucketAsync`
- `DeleteBucketAsync`
- `GetBucketVersioningAsync`
- `SetBucketVersioningAsync`
- `ListObjectVersionsAsync`
- `GetBucketLifecycleAsync`
- `PutBucketLifecycleAsync`
- `DeleteBucketLifecycleAsync`
- `GetBucketCorsAsync`
- `PutBucketCorsAsync`
- `DeleteBucketCorsAsync`
- `GetBucketNotificationAsync`
- `PutBucketNotificationAsync`
- `DeleteBucketNotificationAsync`
- `ListObjectsAsync`
- `PutObjectAsync`
- `GetObjectAsync`
- `HeadObjectAsync`
- `DeleteObjectAsync`
- `CopyObjectAsync`
- `GetObjectTaggingAsync`
- `PutObjectTaggingAsync`
- `DeleteObjectTaggingAsync`
- `InitiateMultipartUploadAsync`
- `UploadPartAsync`
- `UploadPartCopyAsync`
- `CompleteMultipartUploadAsync`
- `AbortMultipartUploadAsync`
- `ListPartsAsync`
- `ListMultipartUploadsAsync`
- `UploadObjectMultipartAsync`
- `CreatePresignedGetUrlAsync`
- `CreatePresignedPutUrlAsync`
- `CreatePresignedUploadPartUrlAsync`

Synchronous presign helpers are also available as `CreatePresignedGetUrl`, `CreatePresignedPutUrl`, and `CreatePresignedUploadPartUrl` because URL signing does not perform I/O.

## Multipart upload

```csharp
await using var largeFile = File.OpenRead("large-video.mp4");
var completed = await client.UploadObjectMultipartAsync(
    "photos",
    "video/large-video.mp4",
    largeFile,
    contentType: "video/mp4");

Console.WriteLine(completed.ETag);
```

The high-level helper requires a readable, seekable stream, uses 16 MiB parts by default, and aborts the upload if a part or completion fails. Low-level multipart methods are available when callers need custom scheduling, `UploadPartCopy`, pagination, or presigned part URLs.

## Versioning and lifecycle

```csharp
await client.SetBucketVersioningAsync("photos", "Enabled");
var versions = await client.ListObjectVersionsAsync("photos", prefix: "2026/");
await using var oldVersion = await client.GetObjectAsync("photos", "2026/image.jpg", versions.Versions[0].VersionId);

await client.PutBucketLifecycleAsync("photos", new BucketLifecycleConfiguration
{
    Rules =
    {
        new LifecycleRule
        {
            Id = "expire-logs",
            Status = "Enabled",
            Prefix = "logs/",
            ExpirationDays = 30,
            NoncurrentVersionExpirationDays = 7,
            AbortIncompleteMultipartUploadDays = 1
        }
    }
});
```

Lifecycle day values must be positive integers. Empty lifecycle configurations are rejected; use `DeleteBucketLifecycleAsync` to remove rules.

## Tagging, CORS, and notification

```csharp
await client.PutObjectTaggingAsync(
    "photos",
    "2026/image.jpg",
    new Dictionary<string, string> { ["kind"] = "raw" });

var tags = await client.GetObjectTaggingAsync("photos", "2026/image.jpg");

await client.PutBucketCorsAsync(
    "photos",
    "<CORSConfiguration><CORSRule><AllowedOrigin>*</AllowedOrigin><AllowedMethod>GET</AllowedMethod></CORSRule></CORSConfiguration>");
```

Bucket CORS and notification methods use raw XML so callers can pass through S3-compatible configuration variants without waiting for SDK model updates.

## Presigned URLs

```csharp
var presignedGet = client.CreatePresignedGetUrl(
    "photos",
    "2026/image.jpg",
    TimeSpan.FromMinutes(15));

var versionedGet = client.CreatePresignedGetUrl(
    "photos",
    "2026/image.jpg",
    versionId: "object-version-id",
    expires: TimeSpan.FromMinutes(15));

Console.WriteLine(presignedGet.Url);
```

SigV4 presigned URLs require credentials and support expirations up to seven days.

## Error handling

Means returns S3-style XML error envelopes. Non-success responses throw `MeansError`:

```csharp
try
{
    await client.HeadObjectAsync("photos", "missing.jpg");
}
catch (MeansError error) when (error.Code == "NoSuchKey")
{
    Console.WriteLine(error.RequestId);
}
```

## Addressing

Path-style addressing is enabled by default:

```text
https://api.means.local/bucket/key
```

Set `ForcePathStyle = false` to use virtual-hosted-style addressing for DNS-compatible endpoints:

```text
https://bucket.means.local/key
```

For the common Means layout where path-style requests go to `api.means.local` and virtual-hosted requests go to `bucket.means.local`, the SDK infers the suffix automatically. You can also set it explicitly:

```csharp
ForcePathStyle = false,
VirtualHostedDomainSuffix = "means.local"
```
