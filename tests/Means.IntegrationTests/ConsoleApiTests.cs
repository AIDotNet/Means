using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Means.Protocol.S3;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Means.IntegrationTests;

public sealed class ConsoleApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ConsoleWorkflowUsesCookieAuthAndSameOriginPresignedTransfers()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var anonymousSession = await client.GetAsync("/api/console/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSession.StatusCode);

        var deniedLogin = await client.PostAsJsonAsync("/api/console/auth/login", new LoginRequest("admin", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);

        var login = await client.PostAsJsonAsync("/api/console/auth/login", new LoginRequest("admin", "meansadmin"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await ReadJsonAsync<SessionResponse>(login);
        Assert.True(session.Authenticated);
        Assert.Equal("admin", session.UserName);

        var settingsResponse = await client.GetAsync("/api/console/settings");
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var settings = await ReadJsonAsync<SystemSettingsResponse>(settingsResponse);
        Assert.Equal(64 * 1024 * 1024, settings.MaxUploadSizeBytes);
        Assert.True(settings.MinimumMaxUploadSizeBytes < settings.MaximumMaxUploadSizeBytes);

        var invalidSettings = await client.PutAsJsonAsync("/api/console/settings", new UpdateSystemSettingsRequest(1024));
        Assert.Equal(HttpStatusCode.BadRequest, invalidSettings.StatusCode);

        var updateSettings = await client.PutAsJsonAsync(
            "/api/console/settings",
            new UpdateSystemSettingsRequest(128 * 1024 * 1024));
        Assert.Equal(HttpStatusCode.OK, updateSettings.StatusCode);
        var updatedSettings = await ReadJsonAsync<SystemSettingsResponse>(updateSettings);
        Assert.Equal(128 * 1024 * 1024, updatedSettings.MaxUploadSizeBytes);

        var createdBucketResponse = await client.PostAsJsonAsync("/api/console/buckets", new CreateBucketRequest("console-bucket"));
        Assert.Equal(HttpStatusCode.Created, createdBucketResponse.StatusCode);
        var createdBucket = await ReadJsonAsync<BucketUsageResponse>(createdBucketResponse);
        Assert.Equal("console-bucket", createdBucket.BucketName);
        Assert.Equal(0, createdBucket.ObjectCount);

        var defaultSettings = await client.GetAsync("/api/console/buckets/console-bucket/settings");
        Assert.Equal(HttpStatusCode.OK, defaultSettings.StatusCode);
        var defaultBucketSettings = await ReadJsonAsync<BucketSettingsResponse>(defaultSettings);
        Assert.Empty(defaultBucketSettings.DefaultResponseHeaders);
        Assert.Empty(defaultBucketSettings.DefaultMetadata);

        var bucketSettings = await client.PutAsJsonAsync(
            "/api/console/buckets/console-bucket/settings",
            new BucketSettingsRequest(
                new Dictionary<string, string>
                {
                    ["Cache-Control"] = "public, max-age=60",
                    ["Content-Disposition"] = "inline"
                },
                new Dictionary<string, string>
                {
                    ["owner"] = "console"
                }));
        Assert.Equal(HttpStatusCode.OK, bucketSettings.StatusCode);
        var updatedBucketSettings = await ReadJsonAsync<BucketSettingsResponse>(bucketSettings);
        Assert.Equal("public, max-age=60", updatedBucketSettings.DefaultResponseHeaders["Cache-Control"]);
        Assert.Equal("console", updatedBucketSettings.DefaultMetadata["owner"]);

        var upload = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-upload",
            new PresignRequest("folder/from-browser.txt", 900));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var uploadUrl = await ReadJsonAsync<PresignedTransferResponse>(upload);
        Assert.Equal("PUT", uploadUrl.Method);
        Assert.StartsWith("/s3/console-bucket/folder/from-browser.txt?", uploadUrl.Url, StringComparison.Ordinal);

        var put = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, uploadUrl.Url)
        {
            Content = new StringContent("console upload", Encoding.UTF8, "text/plain")
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var objects = await client.GetAsync("/api/console/buckets/console-bucket/objects?prefix=folder/&delimiter=/");
        Assert.Equal(HttpStatusCode.OK, objects.StatusCode);
        var listed = await ReadJsonAsync<ListObjectsResponse>(objects);
        Assert.Contains(listed.Objects, item => item.Key == "folder/from-browser.txt");

        var detail = await client.GetAsync("/api/console/buckets/console-bucket/objects/detail?key=folder%2Ffrom-browser.txt");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var objectInfo = await ReadJsonAsync<ObjectInfoResponse>(detail);
        Assert.Equal("text/plain; charset=utf-8", objectInfo.ContentType);

        var multipartInitiate = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/multipart/initiate",
            new InitiateMultipartRequest("folder/large-from-browser.bin", "application/octet-stream"));
        Assert.Equal(HttpStatusCode.OK, multipartInitiate.StatusCode);
        var multipart = await ReadJsonAsync<MultipartUploadResponse>(multipartInitiate);
        Assert.Equal("folder/large-from-browser.bin", multipart.Key);
        Assert.NotEmpty(multipart.UploadId);

        var multipartPart1 = new byte[5 * 1024 * 1024];
        Array.Fill<byte>(multipartPart1, 0x61);
        var multipartPart2 = Encoding.UTF8.GetBytes("browser-tail");
        var part1 = await UploadMultipartPartFromConsoleAsync(client, multipart, 1, multipartPart1);
        var part2 = await UploadMultipartPartFromConsoleAsync(client, multipart, 2, multipartPart2);

        var multipartComplete = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/multipart/complete",
            new CompleteMultipartRequest(
                multipart.Key,
                multipart.UploadId,
                new[] { new CompletedPartRequest(1, part1), new CompletedPartRequest(2, part2) }));
        Assert.Equal(HttpStatusCode.OK, multipartComplete.StatusCode);

        var multipartDownload = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-download",
            new PresignRequest("folder/large-from-browser.bin", 900));
        Assert.Equal(HttpStatusCode.OK, multipartDownload.StatusCode);
        var multipartDownloadUrl = await ReadJsonAsync<PresignedTransferResponse>(multipartDownload);
        var multipartDownloadedObject = await client.GetAsync(multipartDownloadUrl.Url);
        Assert.Equal(multipartPart1.Length + multipartPart2.Length, (await multipartDownloadedObject.Content.ReadAsByteArrayAsync()).Length);

        var multipartAbortInitiate = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/multipart/initiate",
            new InitiateMultipartRequest("folder/aborted.bin", "application/octet-stream"));
        Assert.Equal(HttpStatusCode.OK, multipartAbortInitiate.StatusCode);
        var abortedMultipart = await ReadJsonAsync<MultipartUploadResponse>(multipartAbortInitiate);
        var multipartAbort = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/multipart/abort",
            new AbortMultipartRequest(abortedMultipart.Key, abortedMultipart.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, multipartAbort.StatusCode);

        var download = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-download",
            new PresignRequest("folder/from-browser.txt", 900));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var downloadUrl = await ReadJsonAsync<PresignedTransferResponse>(download);
        var downloadedObject = await client.GetAsync(downloadUrl.Url);
        Assert.Equal("console upload", await downloadedObject.Content.ReadAsStringAsync());
        Assert.Equal("public, max-age=60", downloadedObject.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("inline", downloadedObject.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("console", downloadedObject.Headers.GetValues("x-amz-meta-owner").Single());

        var copy = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/copy",
            new CopyRequest("console-bucket", "folder/from-browser.txt", "folder/copy.txt"));
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);

        var deleteCopy = await client.DeleteAsync("/api/console/buckets/console-bucket/objects?key=folder%2Fcopy.txt");
        Assert.Equal(HttpStatusCode.NoContent, deleteCopy.StatusCode);

        var publicReadPolicy = """
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": "*",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::console-bucket/*"
                }
              ]
            }
            """;
        var putPolicy = await client.PutAsJsonAsync("/api/console/buckets/console-bucket/policy", new PolicyRequest(publicReadPolicy));
        Assert.Equal(HttpStatusCode.NoContent, putPolicy.StatusCode);

        var anonymousObject = await client.GetAsync("/s3/console-bucket/folder/from-browser.txt");
        Assert.Equal(HttpStatusCode.OK, anonymousObject.StatusCode);
        Assert.Equal("console upload", await anonymousObject.Content.ReadAsStringAsync());

        var deletePolicy = await client.DeleteAsync("/api/console/buckets/console-bucket/policy");
        Assert.Equal(HttpStatusCode.NoContent, deletePolicy.StatusCode);

        var deniedAnonymousObject = await client.GetAsync("/s3/console-bucket/folder/from-browser.txt");
        Assert.Equal(HttpStatusCode.Forbidden, deniedAnonymousObject.StatusCode);

        var dashboard = await client.GetAsync("/api/console/dashboard-stats");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        using var dashboardDocument = JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync());
        var dashboardRoot = dashboardDocument.RootElement;
        var hourly = dashboardRoot.GetProperty("hourly").EnumerateArray().ToArray();
        Assert.Equal(24, hourly.Length);
        Assert.Contains(hourly, point => point.GetProperty("requestCount").GetInt64() == 0);
        Assert.True(dashboardRoot.GetProperty("capacity").GetProperty("totalBytes").GetInt64() > 0);
        Assert.Equal(1, dashboardRoot.GetProperty("nodes").GetProperty("serversOnline").GetInt32());
        Assert.True(hourly.Sum(point => point.GetProperty("requestCount").GetInt64()) >= 3);
        Assert.True(hourly.Sum(point => point.GetProperty("errorCount").GetInt64()) >= 1);
        Assert.True(hourly.Sum(point => point.GetProperty("ingressBytes").GetInt64()) >= Encoding.UTF8.GetByteCount("console upload"));
        Assert.True(hourly.Sum(point => point.GetProperty("egressBytes").GetInt64()) >= Encoding.UTF8.GetByteCount("console upload"));
        Assert.Contains(
            dashboardRoot.GetProperty("recentBuckets").EnumerateArray(),
            bucket => bucket.GetProperty("bucketName").GetString() == "console-bucket");

        var bucketSummary = await client.GetAsync("/api/console/buckets/console-bucket/summary");
        Assert.Equal(HttpStatusCode.OK, bucketSummary.StatusCode);
        using var bucketSummaryDocument = JsonDocument.Parse(await bucketSummary.Content.ReadAsStringAsync());
        var summary = bucketSummaryDocument.RootElement.GetProperty("summary");
        Assert.Equal("console-bucket", summary.GetProperty("bucketName").GetString());
        Assert.True(summary.GetProperty("objectCount").GetInt64() >= 1);
        Assert.True(summary.GetProperty("requestCount").GetInt64() >= 3);

        var accessKeyResponse = await client.PostAsJsonAsync("/api/console/access-keys", new AccessKeyRequest("console-test-key"));
        Assert.Equal(HttpStatusCode.Created, accessKeyResponse.StatusCode);
        var accessKey = await ReadJsonAsync<AccessKeySecretResponse>(accessKeyResponse);
        Assert.Equal("console-test-key", accessKey.AccessKey);
        Assert.NotEmpty(accessKey.SecretKey);

        var listKeysResponse = await client.GetAsync("/api/console/access-keys");
        Assert.Equal(HttpStatusCode.OK, listKeysResponse.StatusCode);
        var listKeysJson = await listKeysResponse.Content.ReadAsStringAsync();
        Assert.Contains("console-test-key", listKeysJson);
        Assert.DoesNotContain(accessKey.SecretKey, listKeysJson);

        var keyCredentials = new SigV4SigningCredentials(accessKey.AccessKey, accessKey.SecretKey);
        var signedCreate = new HttpRequestMessage(HttpMethod.Put, "https://localhost/s3/key-owned");
        SigV4RequestSigner.Sign(signedCreate, keyCredentials, now: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(signedCreate)).StatusCode);

        var deleteKey = await client.DeleteAsync("/api/console/access-keys/console-test-key");
        Assert.Equal(HttpStatusCode.NoContent, deleteKey.StatusCode);

        var signedAfterDelete = new HttpRequestMessage(HttpMethod.Head, "https://localhost/s3/key-owned");
        SigV4RequestSigner.Sign(signedAfterDelete, keyCredentials, now: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(signedAfterDelete)).StatusCode);

        var audit = await client.GetAsync("/api/console/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var entries = await ReadJsonAsync<AuditEntryResponse[]>(audit);
        Assert.Contains(entries, entry => entry.Action == "auth.login" && entry.Status == "success");
        Assert.Contains(entries, entry => entry.Action == "settings.update" && entry.Status == "success");
        Assert.Contains(entries, entry => entry.Action == "object.presign-upload");
        Assert.Contains(entries, entry => entry.Action == "object.presign-download");
        Assert.Contains(entries, entry => entry.Action == "object.multipart.complete");
        Assert.Contains(entries, entry => entry.Action == "object.multipart.abort");
        Assert.Contains(entries, entry => entry.Action == "bucket.settings.update");
        Assert.Contains(entries, entry => entry.Action == "access-key.delete");

        var fallback = await client.GetAsync("/buckets/console-bucket");
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Contains("id=\"root\"", await fallback.Content.ReadAsStringAsync());

        var s3HostResponse = await client.GetAsync("https://api.means.local/not-a-spa-route");
        Assert.Equal(HttpStatusCode.BadRequest, s3HostResponse.StatusCode);
        Assert.Contains("<Error>", await s3HostResponse.Content.ReadAsStringAsync());
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        Assert.NotNull(result);
        return result;
    }

    private static async Task<string> UploadMultipartPartFromConsoleAsync(
        HttpClient client,
        MultipartUploadResponse multipart,
        int partNumber,
        byte[] content)
    {
        var presign = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/multipart/presign-part",
            new PresignMultipartPartRequest(multipart.Key, multipart.UploadId, partNumber, 900));
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var transfer = await ReadJsonAsync<PresignedTransferResponse>(presign);
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, transfer.Url)
        {
            Content = new ByteArrayContent(content)
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag?.Tag.Trim('"') ?? "";
    }

    private sealed class MeansWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "means-tests", Guid.NewGuid().ToString("N"));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Means:Storage:DatabasePath"] = Path.Combine(_root, "means.db"),
                    ["Means:Storage:ObjectsPath"] = Path.Combine(_root, "objects"),
                    ["Means:Storage:DefaultAccessKey"] = "meansadmin",
                    ["Means:Storage:DefaultSecretKey"] = "meansadminsecret",
                    ["Means:S3:ServiceHost"] = "api.means.local",
                    ["Means:S3:DomainSuffix"] = "means.local",
                    ["Means:S3:AliasPrefix"] = "/s3",
                    ["Means:RequestLimits:MaxUploadSizeBytes"] = (64 * 1024 * 1024).ToString()
                });
            });

            return base.CreateHost(builder);
        }
    }

    private sealed record LoginRequest(string UserName, string Password);

    private sealed record SessionResponse(bool Authenticated, string UserName);

    private sealed record CreateBucketRequest(string BucketName);

    private sealed record BucketUsageResponse(string BucketName, DateTimeOffset CreatedAt, long ObjectCount, long TotalBytes);

    private sealed record BucketSettingsRequest(
        IReadOnlyDictionary<string, string> DefaultResponseHeaders,
        IReadOnlyDictionary<string, string> DefaultMetadata);

    private sealed record BucketSettingsResponse(
        string BucketName,
        IReadOnlyDictionary<string, string> DefaultResponseHeaders,
        IReadOnlyDictionary<string, string> DefaultMetadata,
        DateTimeOffset? UpdatedAt);

    private sealed record PresignRequest(string Key, int ExpiresSeconds);

    private sealed record PresignedTransferResponse(string Method, string Url, int ExpiresSeconds);

    private sealed record InitiateMultipartRequest(string Key, string ContentType);

    private sealed record MultipartUploadResponse(string BucketName, string Key, string UploadId);

    private sealed record PresignMultipartPartRequest(string Key, string UploadId, int PartNumber, int ExpiresSeconds);

    private sealed record CompleteMultipartRequest(string Key, string UploadId, IReadOnlyList<CompletedPartRequest> Parts);

    private sealed record CompletedPartRequest(int PartNumber, string ETag);

    private sealed record AbortMultipartRequest(string Key, string UploadId);

    private sealed record ListObjectsResponse(IReadOnlyList<ListedObjectResponse> Objects);

    private sealed record ListedObjectResponse(string Key, long Size, string ContentType);

    private sealed record ObjectInfoResponse(string Key, long ContentLength, string ContentType);

    private sealed record CopyRequest(string SourceBucket, string SourceKey, string DestinationKey);

    private sealed record PolicyRequest(string Policy);

    private sealed record AccessKeyRequest(string AccessKey);

    private sealed record AccessKeySecretResponse(string AccessKey, string SecretKey, bool Enabled, DateTimeOffset CreatedAt);

    private sealed record AuditEntryResponse(string Action, string Resource, string Status);

    private sealed record UpdateSystemSettingsRequest(long MaxUploadSizeBytes);

    private sealed record SystemSettingsResponse(long MaxUploadSizeBytes, long MinimumMaxUploadSizeBytes, long MaximumMaxUploadSizeBytes);
}
