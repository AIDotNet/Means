using System.Text;
using Means;

var endpoint = GetEnv("MEANS_ENDPOINT", "http://localhost:5178/s3/");
var accessKey = GetEnv("MEANS_ACCESS_KEY", "meansadmin");
var secretKey = GetEnv("MEANS_SECRET_KEY", "meansadminsecret");
var bucket = GetEnv("MEANS_BUCKET", "sdk-examples");

using var client = new MeansClient(new MeansClientOptions
{
    Endpoint = new Uri(endpoint),
    Credentials = new MeansCredentials(accessKey, secretKey),
    Region = "us-east-1",
    ForcePathStyle = true
});

await EnsureBucketAsync(client, bucket);

var key = $"notes/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt";
await using var upload = new MemoryStream(Encoding.UTF8.GetBytes("Hello from the Means C# SDK example."));
var put = await client.PutObjectAsync(
    bucket,
    key,
    upload,
    contentType: "text/plain",
    metadata: new Dictionary<string, string> { ["example"] = "csharp-basic" });
Console.WriteLine($"Uploaded {bucket}/{key}, ETag={put.ETag}");

var objects = await client.ListObjectsAsync(bucket, prefix: "notes/");
Console.WriteLine($"Found {objects.Objects.Count} object(s) under notes/.");

await using (var downloaded = await client.GetObjectAsync(bucket, key))
using (var reader = new StreamReader(downloaded.Content, Encoding.UTF8))
{
    Console.WriteLine($"Downloaded text: {await reader.ReadToEndAsync()}");
}

var presignedPut = client.CreatePresignedPutUrl(bucket, "incoming/presigned.txt", TimeSpan.FromMinutes(10));
using (var http = new HttpClient())
using (var content = new StringContent("Uploaded with a presigned PUT URL.", Encoding.UTF8, "text/plain"))
using (var response = await http.PutAsync(presignedPut.Url, content))
{
    response.EnsureSuccessStatusCode();
}

var presignedGet = client.CreatePresignedGetUrl(bucket, "incoming/presigned.txt", TimeSpan.FromMinutes(10));
Console.WriteLine($"Presigned GET URL: {presignedGet.Url}");

await client.SetBucketVersioningAsync(bucket, "Enabled");
var versions = await client.ListObjectVersionsAsync(bucket, prefix: "incoming/");
var deleteMarkerCount = versions.Versions.Count(version => version.IsDeleteMarker);
Console.WriteLine($"Versioned entries under incoming/: {versions.Versions.Count}, delete markers: {deleteMarkerCount}");

await client.PutBucketLifecycleAsync(bucket, new BucketLifecycleConfiguration
{
    Rules =
    {
        new LifecycleRule
        {
            Id = "expire-example-temp",
            Status = "Enabled",
            Prefix = "tmp/",
            ExpirationDays = 7,
            NoncurrentVersionExpirationDays = 3,
            AbortIncompleteMultipartUploadDays = 1
        }
    }
});

await using var multipartBody = new MemoryStream(Encoding.UTF8.GetBytes(new string('m', 1024 * 1024)));
var multipart = await client.UploadObjectMultipartAsync(
    bucket,
    "multipart/small-demo.txt",
    multipartBody,
    contentType: "text/plain");
Console.WriteLine($"Multipart upload completed, ETag={multipart.ETag}");

static string GetEnv(string name, string fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static async Task EnsureBucketAsync(MeansClient client, string bucket)
{
    try
    {
        await client.CreateBucketAsync(bucket);
        Console.WriteLine($"Created bucket {bucket}.");
    }
    catch (MeansError error) when (error.Code is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou")
    {
        Console.WriteLine($"Bucket {bucket} already exists.");
    }
}
