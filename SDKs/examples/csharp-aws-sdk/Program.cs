using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

var endpoint = GetEnv("MEANS_ENDPOINT", "http://localhost:5178/s3/");
var accessKey = GetEnv("MEANS_ACCESS_KEY", "meansadmin");
var secretKey = GetEnv("MEANS_SECRET_KEY", "meansadminsecret");
var bucket = GetEnv("MEANS_BUCKET", "sdk-examples");
var checkBucket = Environment.GetEnvironmentVariable("MEANS_BUCKET");
var region = GetEnv("AWS_REGION", GetEnv("AWS_DEFAULT_REGION", "us-east-1"));
var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant();

using var client = new AmazonS3Client(
    accessKey,
    secretKey,
    new AmazonS3Config
    {
        ServiceURL = endpoint,
        AuthenticationRegion = region,
        ForcePathStyle = true,
        UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    });

PrintConfiguration(endpoint, region);

if (mode is "check" or "preflight")
{
    Environment.ExitCode = await RunCompatibilityCheckAsync(client, checkBucket, endpoint);
    return;
}

await EnsureBucketAsync(client, bucket);

var key = $"aws-sdk-dotnet/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt";
await using var upload = new MemoryStream(Encoding.UTF8.GetBytes("Hello from the official AWS SDK for .NET."));
var putRequest = new PutObjectRequest
{
    BucketName = bucket,
    Key = key,
    InputStream = upload,
    ContentType = "text/plain"
};
putRequest.Metadata.Add("example", "aws-sdk-dotnet");

var put = await client.PutObjectAsync(putRequest);
Console.WriteLine($"Uploaded {bucket}/{key}, ETag={put.ETag}");

var listed = await client.ListObjectsV2Async(new ListObjectsV2Request
{
    BucketName = bucket,
    Prefix = "aws-sdk-dotnet/"
});
Console.WriteLine($"Found {listed.S3Objects.Count} object(s) under aws-sdk-dotnet/.");

using (var downloaded = await client.GetObjectAsync(bucket, key))
using (var reader = new StreamReader(downloaded.ResponseStream, Encoding.UTF8))
{
    Console.WriteLine($"Downloaded text: {await reader.ReadToEndAsync()}");
}

var presignedGetUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
{
    BucketName = bucket,
    Key = key,
    Verb = HttpVerb.GET,
    Expires = DateTime.UtcNow.AddMinutes(10)
});
Console.WriteLine($"Presigned GET URL: {presignedGetUrl}");

static void PrintConfiguration(string endpoint, string region)
{
    Console.WriteLine("Means S3 compatibility configuration");
    Console.WriteLine($"  endpoint: {endpoint}");
    Console.WriteLine($"  region: {region}");
    Console.WriteLine("  forcePathStyle: true");
    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
    {
        Console.WriteLine($"  path prefix: {uri.AbsolutePath}");
        if (uri.AbsolutePath is "/" or "")
        {
            Console.WriteLine("  note: if your Means data plane is mounted under /s3/, include /s3/ in MEANS_ENDPOINT.");
        }
    }
}

static async Task<int> RunCompatibilityCheckAsync(IAmazonS3 client, string? bucket, string endpoint)
{
    try
    {
        var buckets = await client.ListBucketsAsync();
        Console.WriteLine($"S3 ListBuckets succeeded ({buckets.Buckets.Count} bucket(s)).");

        if (!string.IsNullOrWhiteSpace(bucket))
        {
            var listed = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = 1
            });
            Console.WriteLine($"S3 ListObjectsV2 succeeded for {bucket} ({listed.S3Objects.Count} sampled object(s)).");

            var presignKey = GetEnv("MEANS_PRESIGN_KEY", GetEnv("MEANS_KEY", "sdk-preflight.txt"));
            var presignedGetUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = presignKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(5)
            });
            PrintPresignedUrlCheck(endpoint, presignedGetUrl, bucket, presignKey);
        }
        else
        {
            Console.WriteLine("Set MEANS_BUCKET to also run signed bucket read and presigned URL checks.");
        }

        return 0;
    }
    catch (AmazonS3Exception error)
    {
        Console.Error.WriteLine($"S3 compatibility check failed: {error.ErrorCode} ({(int)error.StatusCode})");
        Console.Error.WriteLine(error.Message);
        Console.Error.WriteLine("Troubleshooting:");
        Console.Error.WriteLine("- MEANS_ENDPOINT must point to the S3 data plane, for example http://localhost:5178/s3/.");
        Console.Error.WriteLine("- Keep ForcePathStyle enabled for local, path-prefixed, or load-balanced deployments.");
        Console.Error.WriteLine("- Use the same signing region on clients and presigners, commonly us-east-1.");
        Console.Error.WriteLine("- Check MEANS_ACCESS_KEY and MEANS_SECRET_KEY against the console access key.");
        Console.Error.WriteLine("- If presigned URLs fail but ListBuckets works, compare the endpoint path prefix used by the presigner.");
        Console.Error.WriteLine("- For load-balanced clusters, presign with the public S3 origin and keep the proxy host/path unchanged.");
        return 1;
    }
}

static void PrintPresignedUrlCheck(string endpoint, string presignedGetUrl, string bucket, string key)
{
    Console.WriteLine($"S3 presigned GET URL generated for {bucket}/{key} (not fetched).");

    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
        !Uri.TryCreate(presignedGetUrl, UriKind.Absolute, out var signedUri))
    {
        Console.WriteLine("  presigned URL shape check skipped because endpoint or URL is not absolute.");
        return;
    }

    var endpointOrigin = endpointUri.GetLeftPart(UriPartial.Authority);
    var signedOrigin = signedUri.GetLeftPart(UriPartial.Authority);
    var endpointPrefix = NormalizePathPrefix(endpointUri.AbsolutePath);

    Console.WriteLine($"  presigned origin: {signedOrigin}");
    Console.WriteLine($"  presigned path: {signedUri.AbsolutePath}");

    if (!string.Equals(endpointOrigin, signedOrigin, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  warning: presigned origin differs from MEANS_ENDPOINT origin ({endpointOrigin}). For browsers and external clients, presign with the public load-balanced origin.");
    }

    if (endpointPrefix.Length == 0)
    {
        Console.WriteLine("  endpoint path prefix preserved: not applicable");
        return;
    }

    var hasEndpointPrefix =
        string.Equals(signedUri.AbsolutePath, endpointPrefix, StringComparison.Ordinal) ||
        signedUri.AbsolutePath.StartsWith($"{endpointPrefix}/", StringComparison.Ordinal);
    Console.WriteLine($"  endpoint path prefix preserved: {(hasEndpointPrefix ? "yes" : "no")}");

    if (!hasEndpointPrefix)
    {
        Console.WriteLine($"  warning: MEANS_ENDPOINT uses path prefix {endpointPrefix}, but the presigned URL path does not. Keep /s3/ or any gateway prefix in the endpoint used by presigners.");
    }
}

static string NormalizePathPrefix(string path)
{
    if (string.IsNullOrWhiteSpace(path) || path == "/")
    {
        return string.Empty;
    }

    return path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
}

static string GetEnv(string name, string fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static async Task EnsureBucketAsync(IAmazonS3 client, string bucket)
{
    try
    {
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        Console.WriteLine($"Created bucket {bucket}.");
    }
    catch (AmazonS3Exception error) when (error.ErrorCode is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou")
    {
        Console.WriteLine($"Bucket {bucket} already exists.");
    }
}
