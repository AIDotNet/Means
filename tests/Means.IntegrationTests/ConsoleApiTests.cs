using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Means.Core;
using Means.Protocol.S3;
using Means.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Means.IntegrationTests;

public sealed class ConsoleApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ClusterTopologyMarksStaleHeartbeatOffline()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var store = factory.Services.GetRequiredService<IClusterStore>();
        var topology = await WaitForRegisteredNodeAsync(store);
        Assert.Single(topology.Nodes);
        Assert.Equal(ClusterNodeStatuses.Online, topology.Nodes[0].Status);

        var staleTopology = await store.GetClusterTopologyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Single(staleTopology.Nodes);
        Assert.Equal(ClusterNodeStatuses.Offline, staleTopology.Nodes[0].Status);
        Assert.All(staleTopology.Nodes[0].Disks, disk => Assert.Equal(StorageDiskStatuses.Offline, disk.Status));
    }

    [Fact]
    public async Task ClusterTopologyFiltersClustersAndMarksMissingDisksOffline()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var store = factory.Services.GetRequiredService<IClusterStore>();
        _ = await WaitForRegisteredNodeAsync(store);
        var now = DateTimeOffset.UtcNow;

        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "test-cluster",
                "Test Cluster",
                "manual-node",
                "manual-node",
                "https://manual-node",
                "manual-pool",
                "Manual Pool",
                [
                    new StorageDiskRegistration("manual-a", "manual-pool", "/data/manual-a", 100, 80, StorageDiskStatuses.Online),
                    new StorageDiskRegistration("manual-b", "manual-pool", "/data/manual-b", 200, 160, StorageDiskStatuses.Online)
                ],
                now),
            CancellationToken.None);

        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "other-cluster",
                "Other Cluster",
                "other-node",
                "other-node",
                "https://other-node",
                "other-pool",
                "Other Pool",
                [new StorageDiskRegistration("other-a", "other-pool", "/data/other-a", 300, 240, StorageDiskStatuses.Online)],
                now),
            CancellationToken.None);

        await store.RegisterNodeAsync(
            new ClusterNodeRegistration(
                "test-cluster",
                "Test Cluster",
                "manual-node",
                "manual-node",
                "https://manual-node",
                "manual-pool",
                "Manual Pool",
                [new StorageDiskRegistration("manual-a", "manual-pool", "/data/manual-a", 100, 80, StorageDiskStatuses.Online)],
                now.AddSeconds(1)),
            CancellationToken.None);

        var topology = await store.GetClusterTopologyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        Assert.Equal("test-cluster", topology.Cluster.ClusterId);
        Assert.DoesNotContain(topology.Nodes, node => node.ClusterId == "other-cluster");
        Assert.DoesNotContain(topology.Nodes, node => node.NodeId == "other-node");

        var manual = Assert.Single(topology.Nodes, node => node.NodeId == "manual-node");
        Assert.Equal(StorageDiskStatuses.Online, Assert.Single(manual.Disks, disk => disk.DiskId == "manual-a").Status);
        Assert.Equal(StorageDiskStatuses.Offline, Assert.Single(manual.Disks, disk => disk.DiskId == "manual-b").Status);

        var manualPool = Assert.Single(topology.Pools, pool => pool.PoolId == "manual-pool");
        Assert.Equal(2, manualPool.DiskCount);
        Assert.Equal(100, manualPool.TotalBytes);
        Assert.Equal(80, manualPool.AvailableBytes);
    }

    [Fact]
    public async Task UploadConcurrencyLimitRejectsS3PutWhenSlotsAreExhausted()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var limiter = factory.Services.GetRequiredService<UploadConcurrencyLimiter>();
        var lease = await limiter.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(lease);
        await using (lease)
        {
            var response = await client.PutAsync("/s3/concurrency-bucket/object.bin", new ByteArrayContent([1, 2, 3]));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("<Code>SlowDown</Code>", body);
            Assert.Contains("Too many concurrent upload requests.", body);
        }
    }

    [Fact]
    public async Task ConsoleLoginRateLimitReturnsJsonSlowDown()
    {
        await using var factory = new MeansWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Means:RateLimits:ConsoleLoginPermitLimit"] = "1",
            ["Means:RateLimits:ConsoleLoginWindowSeconds"] = "60"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var first = await client.PostAsJsonAsync("/api/console/auth/login", new LoginRequest("admin", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        var limited = await client.PostAsJsonAsync("/api/console/auth/login", new LoginRequest("admin", "wrong-again"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0);
        var error = await ReadJsonAsync<ConsoleErrorResponse>(limited);
        Assert.Equal("SlowDown", error.Code);
        Assert.Equal(429, error.StatusCode);
    }

    [Fact]
    public async Task S3RateLimitReturnsXmlSlowDown()
    {
        await using var factory = new MeansWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Means:RateLimits:S3PermitLimit"] = "1",
            ["Means:RateLimits:S3WindowSeconds"] = "60"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var first = await client.GetAsync("/s3");
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);

        var limited = await client.GetAsync("/s3");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0);
        var body = await limited.Content.ReadAsStringAsync();
        Assert.Contains("<Code>SlowDown</Code>", body);
        Assert.Contains("Rate limit exceeded. Please reduce your request rate.", body);
    }

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

        var defaultVersioning = await client.GetAsync("/api/console/buckets/console-bucket/versioning");
        Assert.Equal(HttpStatusCode.OK, defaultVersioning.StatusCode);
        Assert.Equal(BucketVersioningStatuses.Off, (await ReadJsonAsync<BucketVersioningResponse>(defaultVersioning)).Status);

        var putVersioning = await client.PutAsJsonAsync(
            "/api/console/buckets/console-bucket/versioning",
            new BucketVersioningRequest(BucketVersioningStatuses.Enabled));
        Assert.Equal(HttpStatusCode.OK, putVersioning.StatusCode);
        Assert.Equal(BucketVersioningStatuses.Enabled, (await ReadJsonAsync<BucketVersioningResponse>(putVersioning)).Status);

        var emptyLifecycle = await client.GetAsync("/api/console/buckets/console-bucket/lifecycle");
        Assert.Equal(HttpStatusCode.OK, emptyLifecycle.StatusCode);
        Assert.Empty((await ReadJsonAsync<BucketLifecycleResponse>(emptyLifecycle)).Rules);

        var putLifecycle = await client.PutAsJsonAsync(
            "/api/console/buckets/console-bucket/lifecycle",
            new BucketLifecycleRequest(
            [
                new LifecycleRuleRequest(
                    "expire-console-logs",
                    "Enabled",
                    "logs/",
                    ExpirationDays: 30,
                    NoncurrentVersionExpirationDays: 7,
                    AbortIncompleteMultipartUploadDays: 1)
            ]));
        Assert.Equal(HttpStatusCode.OK, putLifecycle.StatusCode);
        var lifecycle = await ReadJsonAsync<BucketLifecycleResponse>(putLifecycle);
        var lifecycleRule = Assert.Single(lifecycle.Rules);
        Assert.Equal("expire-console-logs", lifecycleRule.Id);
        Assert.Equal(30, lifecycleRule.ExpirationDays);

        var deleteLifecycle = await client.DeleteAsync("/api/console/buckets/console-bucket/lifecycle");
        Assert.Equal(HttpStatusCode.NoContent, deleteLifecycle.StatusCode);
        var lifecycleAfterDelete = await client.GetAsync("/api/console/buckets/console-bucket/lifecycle");
        Assert.Empty((await ReadJsonAsync<BucketLifecycleResponse>(lifecycleAfterDelete)).Rules);

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

        var versionedUpload1 = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-upload",
            new PresignRequest("folder/versioned.txt", 900));
        var versionedUploadUrl1 = await ReadJsonAsync<PresignedTransferResponse>(versionedUpload1);
        var versionedPut1 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, versionedUploadUrl1.Url)
        {
            Content = new StringContent("v1", Encoding.UTF8, "text/plain")
        });
        Assert.Equal(HttpStatusCode.OK, versionedPut1.StatusCode);
        var firstVersionId = versionedPut1.Headers.GetValues("x-amz-version-id").Single();

        var versionedUpload2 = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-upload",
            new PresignRequest("folder/versioned.txt", 900));
        var versionedUploadUrl2 = await ReadJsonAsync<PresignedTransferResponse>(versionedUpload2);
        var versionedPut2 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, versionedUploadUrl2.Url)
        {
            Content = new StringContent("v2", Encoding.UTF8, "text/plain")
        });
        Assert.Equal(HttpStatusCode.OK, versionedPut2.StatusCode);
        var secondVersionId = versionedPut2.Headers.GetValues("x-amz-version-id").Single();
        Assert.NotEqual(firstVersionId, secondVersionId);

        var versionsResponse = await client.GetAsync("/api/console/buckets/console-bucket/objects/versions?prefix=folder%2Fversioned.txt&maxKeys=10");
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        var versions = await ReadJsonAsync<ObjectVersionsResponse>(versionsResponse);
        Assert.Contains(versions.Versions, version => version.VersionId == firstVersionId && !version.IsLatest);
        Assert.Contains(versions.Versions, version => version.VersionId == secondVersionId && version.IsLatest);

        var oldDetail = await client.GetAsync($"/api/console/buckets/console-bucket/objects/detail?key=folder%2Fversioned.txt&versionId={firstVersionId}");
        Assert.Equal(HttpStatusCode.OK, oldDetail.StatusCode);
        Assert.Equal(2, (await ReadJsonAsync<ObjectInfoResponse>(oldDetail)).ContentLength);

        var oldDownload = await client.PostAsJsonAsync(
            "/api/console/buckets/console-bucket/objects/presign-download",
            new PresignRequest("folder/versioned.txt", 900, firstVersionId));
        Assert.Equal(HttpStatusCode.OK, oldDownload.StatusCode);
        var oldDownloadUrl = await ReadJsonAsync<PresignedTransferResponse>(oldDownload);
        Assert.Equal("v1", await (await client.GetAsync(oldDownloadUrl.Url)).Content.ReadAsStringAsync());

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

        var cluster = await client.GetAsync("/api/console/cluster");
        Assert.Equal(HttpStatusCode.OK, cluster.StatusCode);
        using var clusterDocument = JsonDocument.Parse(await cluster.Content.ReadAsStringAsync());
        var clusterRoot = clusterDocument.RootElement;
        Assert.Equal("test-cluster", clusterRoot.GetProperty("cluster").GetProperty("clusterId").GetString());
        Assert.Equal("Test Cluster", clusterRoot.GetProperty("cluster").GetProperty("name").GetString());
        var nodes = clusterRoot.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Single(nodes);
        Assert.Equal("test-node", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal("Online", nodes[0].GetProperty("status").GetString());
        var disks = nodes[0].GetProperty("disks").EnumerateArray().ToArray();
        Assert.Single(disks);
        Assert.Equal("test-objects", disks[0].GetProperty("diskId").GetString());
        Assert.True(disks[0].GetProperty("totalBytes").GetInt64() > 0);
        var pools = clusterRoot.GetProperty("pools").EnumerateArray().ToArray();
        Assert.Single(pools);
        Assert.Equal("Test Pool", pools[0].GetProperty("name").GetString());

        var invalidEcProfile = await client.PutAsJsonAsync(
            "/api/console/ec-profiles/ec-bad",
            new SaveErasureCodingProfileRequest(1, 1, 123, Enabled: true));
        Assert.Equal(HttpStatusCode.BadRequest, invalidEcProfile.StatusCode);

        var saveEcProfile = await client.PutAsJsonAsync(
            "/api/console/ec-profiles/EC-4-2",
            new SaveErasureCodingProfileRequest(4, 2, 1024 * 1024, Enabled: true));
        Assert.Equal(HttpStatusCode.OK, saveEcProfile.StatusCode);
        var ecProfile = await ReadJsonAsync<ErasureCodingProfileResponse>(saveEcProfile);
        Assert.Equal("ec-4-2", ecProfile.ProfileId);
        Assert.Equal(4, ecProfile.DataShards);
        Assert.Equal(2, ecProfile.ParityShards);
        Assert.Equal(6, ecProfile.TotalShards);
        Assert.Equal(1024 * 1024, ecProfile.CellSizeBytes);
        Assert.True(ecProfile.Enabled);

        var ecProfiles = await client.GetAsync("/api/console/ec-profiles");
        Assert.Equal(HttpStatusCode.OK, ecProfiles.StatusCode);
        var listedEcProfiles = await ReadJsonAsync<ErasureCodingProfileResponse[]>(ecProfiles);
        Assert.Contains(listedEcProfiles, profile => profile.ProfileId == "ec-4-2");

        var diagnostics = await client.GetAsync("/api/console/diagnostics");
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        using var diagnosticsDocument = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync());
        var diagnosticsRoot = diagnosticsDocument.RootElement;
        Assert.Equal("test-cluster", diagnosticsRoot.GetProperty("topology").GetProperty("cluster").GetProperty("clusterId").GetString());
        var diagnosticsSummary = diagnosticsRoot.GetProperty("summary");
        Assert.Equal(1, diagnosticsSummary.GetProperty("nodeCount").GetInt32());
        Assert.Equal(1, diagnosticsSummary.GetProperty("onlineNodeCount").GetInt32());
        Assert.True(diagnosticsSummary.GetProperty("bucketCount").GetInt64() >= 1);
        Assert.True(diagnosticsSummary.GetProperty("objectCount").GetInt64() >= 2);
        Assert.True(diagnosticsSummary.GetProperty("totalObjectBytes").GetInt64() >= multipartPart1.Length + multipartPart2.Length);
        var replicaDiagnostics = diagnosticsRoot.GetProperty("objectReplicas");
        Assert.Equal(1, replicaDiagnostics.GetProperty("desiredReplicaCount").GetInt32());
        Assert.True(replicaDiagnostics.GetProperty("replicaRecordCount").GetInt64() >= diagnosticsSummary.GetProperty("objectCount").GetInt64());
        Assert.Equal(0, replicaDiagnostics.GetProperty("missingReplicaFileCount").GetInt64());
        Assert.Equal(0, replicaDiagnostics.GetProperty("underReplicatedObjectCount").GetInt64());
        Assert.Equal(0, replicaDiagnostics.GetProperty("objectsWithoutReplicaManifestCount").GetInt64());
        var repairQueue = diagnosticsRoot.GetProperty("repairQueue");
        Assert.Equal(0, repairQueue.GetProperty("pendingCount").GetInt64());
        Assert.Equal(0, repairQueue.GetProperty("failedCount").GetInt64());
        var metadataDiagnostics = diagnosticsRoot.GetProperty("metadata");
        Assert.Equal(0, metadataDiagnostics.GetProperty("pendingCommitCount").GetInt64());
        Assert.Equal(0, metadataDiagnostics.GetProperty("orphanedReplicaRecordCount").GetInt64());
        var ecDiagnostics = diagnosticsRoot.GetProperty("erasureCoding");
        Assert.Equal(1, ecDiagnostics.GetProperty("profileCount").GetInt64());
        Assert.Equal(1, ecDiagnostics.GetProperty("enabledProfileCount").GetInt64());
        var backgroundTasks = diagnosticsRoot.GetProperty("backgroundTasks").EnumerateArray().ToArray();
        Assert.Contains(backgroundTasks, task => task.GetProperty("taskId").GetString() == "cluster-heartbeat");
        Assert.Contains(backgroundTasks, task => task.GetProperty("taskId").GetString() == "disk-health-isolation");
        Assert.Contains(backgroundTasks, task => task.GetProperty("taskId").GetString() == "replica-repair");
        Assert.Contains(backgroundTasks, task => task.GetProperty("taskId").GetString() == "storage-garbage-collection");
        Assert.Contains(backgroundTasks, task => task.GetProperty("taskId").GetString() == "multipart-cleanup");
        Assert.All(backgroundTasks, task => Assert.True(task.GetProperty("intervalSeconds").GetInt32() > 0));

        var taskManagement = await client.GetAsync("/api/console/background-tasks");
        Assert.Equal(HttpStatusCode.OK, taskManagement.StatusCode);
        using var taskManagementDocument = JsonDocument.Parse(await taskManagement.Content.ReadAsStringAsync());
        var taskManagementRoot = taskManagementDocument.RootElement;
        var taskGroups = taskManagementRoot.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Contains(taskGroups, group => group.GetProperty("category").GetString() == "repair");
        Assert.Contains(taskGroups, group => group.GetProperty("category").GetString() == "rebalance");
        Assert.Contains(taskGroups, group => group.GetProperty("category").GetString() == "lifecycle");
        Assert.Contains(taskGroups, group => group.GetProperty("category").GetString() == "replication");
        var managedTasks = taskManagementRoot.GetProperty("tasks").EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Array, taskManagementRoot.GetProperty("history").ValueKind);
        Assert.Contains(managedTasks, task => task.GetProperty("taskId").GetString() == "erasure-coding-repair");
        Assert.Contains(managedTasks, task => task.GetProperty("taskId").GetString() == "storage-garbage-collection");
        Assert.Contains(managedTasks, task => task.GetProperty("taskId").GetString() == "replica-rebalance");
        Assert.Contains(managedTasks, task => task.GetProperty("taskId").GetString() == "s3-lifecycle");
        Assert.Contains(managedTasks, task => task.GetProperty("taskId").GetString() == "replication-worker");
        Assert.All(managedTasks, task => Assert.True(task.GetProperty("manualRunSupported").GetBoolean()));

        var runScrubTask = await client.PostAsync("/api/console/background-tasks/object-scrub/run", null);
        Assert.Equal(HttpStatusCode.OK, runScrubTask.StatusCode);
        using var runScrubTaskDocument = JsonDocument.Parse(await runScrubTask.Content.ReadAsStringAsync());
        var runScrubTaskRoot = runScrubTaskDocument.RootElement;
        Assert.Equal("object-scrub", runScrubTaskRoot.GetProperty("taskId").GetString());
        Assert.Equal(BackgroundTaskStatuses.Succeeded, runScrubTaskRoot.GetProperty("status").GetString());
        Assert.True(runScrubTaskRoot.GetProperty("successCount").GetInt64() >= 1);
        var taskManagementAfterRun = await client.GetAsync("/api/console/background-tasks");
        Assert.Equal(HttpStatusCode.OK, taskManagementAfterRun.StatusCode);
        using var taskManagementAfterRunDocument = JsonDocument.Parse(await taskManagementAfterRun.Content.ReadAsStringAsync());
        var taskHistory = taskManagementAfterRunDocument.RootElement.GetProperty("history").EnumerateArray().ToArray();
        Assert.Contains(taskHistory, run =>
            run.GetProperty("taskId").GetString() == "object-scrub"
            && run.GetProperty("status").GetString() == BackgroundTaskStatuses.Succeeded);

        var metrics = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.StartsWith("text/plain", metrics.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        var metricsText = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE means_storage_buckets gauge", metricsText);
        Assert.Contains("means_storage_buckets 1", metricsText);
        Assert.Contains("means_cluster_nodes{status=\"online\"} 1", metricsText);
        Assert.Contains("means_object_replica_files{state=\"missing\"} 0", metricsText);
        Assert.Contains("# TYPE means_metadata_pending_commits gauge", metricsText);
        Assert.Contains("means_erasure_coding_profiles{state=\"enabled\"} 1", metricsText);
        Assert.Contains("means_background_task_interval_seconds{task=\"cluster-heartbeat\"} 5", metricsText);
        Assert.Contains("means_background_task_status{status=\"", metricsText);

        var deleteEcProfile = await client.DeleteAsync("/api/console/ec-profiles/ec-4-2");
        Assert.Equal(HttpStatusCode.NoContent, deleteEcProfile.StatusCode);
        var ecProfilesAfterDelete = await client.GetAsync("/api/console/ec-profiles");
        var listedAfterDelete = await ReadJsonAsync<ErasureCodingProfileResponse[]>(ecProfilesAfterDelete);
        Assert.DoesNotContain(listedAfterDelete, profile => profile.ProfileId == "ec-4-2");

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
        Assert.Contains(entries, entry => entry.Action == "bucket.versioning.put");
        Assert.Contains(entries, entry => entry.Action == "bucket.lifecycle.put");
        Assert.Contains(entries, entry => entry.Action == "bucket.lifecycle.delete");
        Assert.Contains(entries, entry => entry.Action == "ec-profile.save");
        Assert.Contains(entries, entry => entry.Action == "ec-profile.delete");
        Assert.Contains(entries, entry => entry.Action == "diagnostics.read");
        Assert.Contains(entries, entry => entry.Action == "background-task.read");
        Assert.Contains(entries, entry => entry.Action == "background-task.run");
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

    private static async Task<ClusterTopology> WaitForRegisteredNodeAsync(IClusterStore store)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var topology = await store.GetClusterTopologyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
            if (topology.Nodes.Count > 0)
            {
                return topology;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Cluster node did not register during test startup.");
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
        private readonly IReadOnlyDictionary<string, string?> _overrides;

        public MeansWebApplicationFactory(IReadOnlyDictionary<string, string?>? overrides = null)
        {
            _overrides = overrides ?? new Dictionary<string, string?>();
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Means:Storage:Backend"] = "SqliteFs",
                    ["Means:Storage:DatabasePath"] = Path.Combine(_root, "means.db"),
                    ["Means:Storage:ObjectsPath"] = Path.Combine(_root, "objects"),
                    ["Means:Storage:DefaultAccessKey"] = "meansadmin",
                    ["Means:Storage:DefaultSecretKey"] = "meansadminsecret",
                    ["Means:S3:ServiceHost"] = "api.means.local",
                    ["Means:S3:DomainSuffix"] = "means.local",
                    ["Means:S3:AliasPrefix"] = "/s3",
                    ["Means:RequestLimits:MaxUploadSizeBytes"] = (64 * 1024 * 1024).ToString(),
                    ["Means:Cluster:ClusterId"] = "test-cluster",
                    ["Means:Cluster:ClusterName"] = "Test Cluster",
                    ["Means:Cluster:NodeId"] = "test-node",
                    ["Means:Cluster:NodeEndpoint"] = "https://api.means.local",
                    ["Means:Cluster:PoolId"] = "test-pool",
                    ["Means:Cluster:PoolName"] = "Test Pool",
                    ["Means:Cluster:ObjectDiskId"] = "test-objects",
                    ["Means:Cluster:HeartbeatIntervalSeconds"] = "5",
                    ["Means:Cluster:OfflineAfterSeconds"] = "60",
                    ["Means:RequestLimits:MaxConcurrentUploadRequests"] = "1"
                };
                foreach (var item in _overrides)
                {
                    values[item.Key] = item.Value;
                }

                configuration.AddInMemoryCollection(values);
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

    private sealed record BucketVersioningRequest(string Status);

    private sealed record BucketVersioningResponse(string BucketName, string Status);

    private sealed record BucketLifecycleRequest(IReadOnlyList<LifecycleRuleRequest> Rules);

    private sealed record BucketLifecycleResponse(IReadOnlyList<LifecycleRuleResponse> Rules);

    private sealed record LifecycleRuleRequest(
        string Id,
        string Status,
        string Prefix,
        int? ExpirationDays,
        int? NoncurrentVersionExpirationDays,
        int? AbortIncompleteMultipartUploadDays);

    private sealed record LifecycleRuleResponse(
        string Id,
        string Status,
        string Prefix,
        int? ExpirationDays,
        int? NoncurrentVersionExpirationDays,
        int? AbortIncompleteMultipartUploadDays);

    private sealed record PresignRequest(string Key, int ExpiresSeconds, string? VersionId = null);

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

    private sealed record ObjectVersionsResponse(IReadOnlyList<ObjectVersionResponse> Versions);

    private sealed record ObjectVersionResponse(string Key, string VersionId, bool IsLatest, bool IsDeleteMarker, long Size);

    private sealed record CopyRequest(string SourceBucket, string SourceKey, string DestinationKey);

    private sealed record PolicyRequest(string Policy);

    private sealed record AccessKeyRequest(string AccessKey);

    private sealed record AccessKeySecretResponse(string AccessKey, string SecretKey, bool Enabled, DateTimeOffset CreatedAt);

    private sealed record AuditEntryResponse(string Action, string Resource, string Status);

    private sealed record ConsoleErrorResponse(string Code, string Message, int StatusCode);

    private sealed record UpdateSystemSettingsRequest(long MaxUploadSizeBytes);

    private sealed record SystemSettingsResponse(long MaxUploadSizeBytes, long MinimumMaxUploadSizeBytes, long MaximumMaxUploadSizeBytes);

    private sealed record SaveErasureCodingProfileRequest(int DataShards, int ParityShards, int CellSizeBytes, bool Enabled);

    private sealed record ErasureCodingProfileResponse(
        string ProfileId,
        int DataShards,
        int ParityShards,
        int CellSizeBytes,
        bool Enabled,
        int TotalShards,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
