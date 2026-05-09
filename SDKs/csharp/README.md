# Means C# SDK

Standalone C# SDK for the Means S3-compatible object storage API. The package targets `net8.0` and `netstandard2.1`, uses `HttpClient`, signs requests with SigV4, parses S3-style XML responses, and throws `MeansError` for S3-style XML errors.

## Install from source

```bash
dotnet pack SDKs/csharp/Means.Client.csproj -c Release
```

Reference the generated `Means.Client` package or project from your application.

## Basic usage

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
- `ObjectHeadResult`
- `PutObjectResult`
- `CopyObjectResult`
- `MultipartUploadResult`
- `UploadPartResult`
- `CompleteMultipartUploadResult`
- `ListPartsResult`
- `ListMultipartUploadsResult`
- `PresignedRequest`
- `MeansError`

`MeansClient` exposes PascalCase async methods for S3-compatible operations:

- `ListBucketsAsync`
- `CreateBucketAsync`
- `HeadBucketAsync`
- `DeleteBucketAsync`
- `ListObjectsAsync`
- `PutObjectAsync`
- `GetObjectAsync`
- `HeadObjectAsync`
- `DeleteObjectAsync`
- `CopyObjectAsync`
- `InitiateMultipartUploadAsync`
- `UploadPartAsync`
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

The high-level helper requires a readable, seekable stream, uses 16 MiB parts by default, and aborts the upload if a part or completion fails. Low-level multipart methods are available when callers need custom scheduling or presigned part URLs.

## Presigned URLs

```csharp
var presignedGet = client.CreatePresignedGetUrl(
    "photos",
    "2026/image.jpg",
    TimeSpan.FromMinutes(15));

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
