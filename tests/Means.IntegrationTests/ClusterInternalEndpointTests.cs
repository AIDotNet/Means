using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Means.IntegrationTests;

public sealed class ClusterInternalEndpointTests
{
    [Fact]
    public async Task InternalShardEndpointStreamsWriteReadAndDelete()
    {
        await using var factory = new MeansWebApplicationFactory();
        var client = factory.CreateClient();
        var payload = Encoding.UTF8.GetBytes("cluster shard bytes");
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var path = "/api/internal/cluster/shards/disk-00/objects/bucket-hash/object-id/shard.00";

        using var put = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent(payload)
        };
        put.Headers.Add("x-means-cluster-token", "cluster-secret");
        put.Headers.Add("x-means-shard-length", payload.Length.ToString());
        put.Headers.Add("x-means-shard-sha256", checksum);
        var putResponse = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);
        Assert.Equal(checksum, putResponse.Headers.GetValues("x-means-shard-sha256").Single());

        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("x-means-cluster-token", "cluster-secret");
        var getResponse = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(payload, await getResponse.Content.ReadAsByteArrayAsync());

        using var head = new HttpRequestMessage(HttpMethod.Head, path);
        head.Headers.Add("x-means-cluster-token", "cluster-secret");
        var headResponse = await client.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(payload.Length, headResponse.Content.Headers.ContentLength);
        Assert.Equal(checksum, headResponse.Headers.GetValues("x-means-shard-sha256").Single());

        using var delete = new HttpRequestMessage(HttpMethod.Delete, path);
        delete.Headers.Add("x-means-cluster-token", "cluster-secret");
        var deleteResponse = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var getAfterDelete = new HttpRequestMessage(HttpMethod.Get, path);
        getAfterDelete.Headers.Add("x-means-cluster-token", "cluster-secret");
        var missingResponse = await client.SendAsync(getAfterDelete);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task InternalShardEndpointRequiresClusterToken()
    {
        await using var factory = new MeansWebApplicationFactory();
        var client = factory.CreateClient();
        var response = await client.PutAsync(
            "/api/internal/cluster/shards/disk-00/objects/bucket-hash/object-id/shard.00",
            new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternalManifestEndpointStreamsWriteReadAndDelete()
    {
        await using var factory = new MeansWebApplicationFactory();
        var client = factory.CreateClient();
        var payload = Encoding.UTF8.GetBytes("""{"formatVersion":1,"objectId":"object-id"}""");
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var path = "/api/internal/cluster/manifests/disk-00/objects/bucket-hash/object-id/xl.meta";

        using var put = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent(payload)
        };
        put.Headers.Add("x-means-cluster-token", "cluster-secret");
        put.Headers.Add("x-means-shard-length", payload.Length.ToString());
        put.Headers.Add("x-means-shard-sha256", checksum);
        var putResponse = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);
        Assert.Equal(checksum, putResponse.Headers.GetValues("x-means-shard-sha256").Single());

        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("x-means-cluster-token", "cluster-secret");
        var getResponse = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("application/json", getResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, await getResponse.Content.ReadAsByteArrayAsync());

        using var head = new HttpRequestMessage(HttpMethod.Head, path);
        head.Headers.Add("x-means-cluster-token", "cluster-secret");
        var headResponse = await client.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(payload.Length, headResponse.Content.Headers.ContentLength);
        Assert.Equal(checksum, headResponse.Headers.GetValues("x-means-shard-sha256").Single());

        using var invalidManifest = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/internal/cluster/manifests/disk-00/objects/bucket-hash/object-id/shard.00")
        {
            Content = new ByteArrayContent(payload)
        };
        invalidManifest.Headers.Add("x-means-cluster-token", "cluster-secret");
        invalidManifest.Headers.Add("x-means-shard-length", payload.Length.ToString());
        invalidManifest.Headers.Add("x-means-shard-sha256", checksum);
        var invalidResponse = await client.SendAsync(invalidManifest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, path);
        delete.Headers.Add("x-means-cluster-token", "cluster-secret");
        var deleteResponse = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    private sealed class MeansWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "means-internal-shards", Guid.NewGuid().ToString("N"));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Means:Storage:ObjectsPath"] = Path.Combine(_root, "objects"),
                    ["Means:Storage:Disks:0"] = Path.Combine(_root, "disk1"),
                    ["Means:Storage:Disks:1"] = Path.Combine(_root, "disk2"),
                    ["Means:Storage:Disks:2"] = Path.Combine(_root, "disk3"),
                    ["Means:Storage:Disks:3"] = Path.Combine(_root, "disk4"),
                    ["Means:Storage:DeploymentId"] = "internal-shard-test",
                    ["Means:Storage:SetId"] = "set-1",
                    ["Means:Storage:ErasureDataShards"] = "2",
                    ["Means:Storage:ErasureParityShards"] = "1",
                    ["Means:Storage:WriteQuorum"] = "2",
                    ["Means:Storage:ReadQuorum"] = "1",
                    ["Means:Storage:DefaultAccessKey"] = "meansadmin",
                    ["Means:Storage:DefaultSecretKey"] = "meansadminsecret",
                    ["Means:Cluster:InternalAuthToken"] = "cluster-secret",
                    ["Means:Cluster:MaxShardTransferBytes"] = (1024 * 1024).ToString(),
                    ["Means:RateLimits:Enabled"] = "false"
                });
            });

            return base.CreateHost(builder);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
