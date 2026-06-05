using System.Net;
using Means.Configuration;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Services;

public sealed class HttpClusterShardTransport : IClusterShardTransport
{
    private const string ClusterTokenHeader = "x-means-cluster-token";
    private const string ShardLengthHeader = "x-means-shard-length";
    private const string ShardChecksumHeader = "x-means-shard-sha256";

    private readonly HttpClient _client;
    private readonly IOptions<ClusterOptions> _options;

    public HttpClusterShardTransport(IOptions<ClusterOptions> options)
    {
        _options = options;
        _client = new HttpClient(CreateHandler(options.Value))
        {
            Timeout = TimeSpan.FromSeconds(NormalizeTimeoutSeconds(options.Value))
        };
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.Value.InternalAuthToken);

    public async Task<ClusterShardWriteResult> WriteShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        Stream content,
        long expectedLength,
        string expectedChecksumSha256,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Put, ClusterFileUri(node, "shards", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        request.Headers.TryAddWithoutValidation(ShardLengthHeader, expectedLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(ShardChecksumHeader, expectedChecksumSha256);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = expectedLength;
        using var response = await SendAsync(request, node, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToTransportExceptionAsync(response, node, cancellationToken);
        }

        return new ClusterShardWriteResult(diskId, NormalizeRelativePath(relativePath), expectedLength, expectedChecksumSha256);
    }

    public async Task<ClusterShardReadResult> OpenShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Get, ClusterFileUri(node, "shards", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        var response = await SendAsync(request, node, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await ToTransportExceptionAsync(response, node, cancellationToken);
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ClusterShardReadResult(
            diskId,
            NormalizeRelativePath(relativePath),
            response.Content.Headers.ContentLength ?? 0,
            stream,
            ContentPath: null,
            Lease: response);
    }

    public Task<ClusterShardStatResult> StatShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return StatClusterFileAsync(node, "shards", diskId, relativePath, cancellationToken);
    }

    public async Task<bool> DeleteShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Delete, ClusterFileUri(node, "shards", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        using var response = await SendAsync(request, node, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ToTransportExceptionAsync(response, node, cancellationToken);
        }

        return true;
    }

    public async Task<ClusterShardWriteResult> WriteManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        Stream content,
        long expectedLength,
        string expectedChecksumSha256,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Put, ClusterFileUri(node, "manifests", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        request.Headers.TryAddWithoutValidation(ShardLengthHeader, expectedLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(ShardChecksumHeader, expectedChecksumSha256);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = expectedLength;
        using var response = await SendAsync(request, node, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToTransportExceptionAsync(response, node, cancellationToken);
        }

        return new ClusterShardWriteResult(diskId, NormalizeRelativePath(relativePath), expectedLength, expectedChecksumSha256);
    }

    public async Task<ClusterShardReadResult> OpenManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Get, ClusterFileUri(node, "manifests", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        var response = await SendAsync(request, node, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await ToTransportExceptionAsync(response, node, cancellationToken);
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ClusterShardReadResult(
            diskId,
            NormalizeRelativePath(relativePath),
            response.Content.Headers.ContentLength ?? 0,
            stream,
            ContentPath: null,
            Lease: response);
    }

    public Task<ClusterShardStatResult> StatManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return StatClusterFileAsync(node, "manifests", diskId, relativePath, cancellationToken);
    }

    public async Task<bool> DeleteManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Delete, ClusterFileUri(node, "manifests", diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        using var response = await SendAsync(request, node, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ToTransportExceptionAsync(response, node, cancellationToken);
        }

        return true;
    }

    private async Task<ClusterShardStatResult> StatClusterFileAsync(
        ClusterNodeInfo node,
        string kind,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(HttpMethod.Head, ClusterFileUri(node, kind, diskId, relativePath));
        request.Headers.TryAddWithoutValidation(ClusterTokenHeader, _options.Value.InternalAuthToken);
        using var response = await SendAsync(request, node, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToTransportExceptionAsync(response, node, cancellationToken);
        }

        var length = response.Content.Headers.ContentLength
            ?? ReadInt64Header(response, ShardLengthHeader)
            ?? 0;
        var checksum = response.Headers.TryGetValues(ShardChecksumHeader, out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : string.Empty;
        return new ClusterShardStatResult(
            diskId,
            NormalizeRelativePath(relativePath),
            length,
            checksum);
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new MeansException(MeansErrorCodes.InvalidRequest, "Cluster shard transport is not configured.", 503);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        ClusterNodeInfo node,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MeansException(
                MeansErrorCodes.SlowDown,
                $"Cluster shard transfer to {node.NodeId} timed out after {NormalizeTimeoutSeconds(_options.Value)} seconds.",
                504);
        }
        catch (HttpRequestException ex)
        {
            throw new MeansException(
                MeansErrorCodes.SlowDown,
                $"Cluster shard transfer to {node.NodeId} failed: {ex.Message}",
                503);
        }
    }

    private static SocketsHttpHandler CreateHandler(ClusterOptions options)
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = NormalizeMaxConnectionsPerNode(options),
            PooledConnectionLifetime = TimeSpan.FromSeconds(NormalizePooledConnectionLifetimeSeconds(options)),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
    }

    private static Uri ClusterFileUri(ClusterNodeInfo node, string kind, string diskId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(node.Endpoint))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Cluster node endpoint is required.", 503);
        }

        var baseUri = node.Endpoint.TrimEnd('/') + "/";
        var path = "api/internal/cluster/"
            + Uri.EscapeDataString(kind)
            + "/"
            + Uri.EscapeDataString(diskId)
            + "/"
            + string.Join('/', NormalizeRelativePath(relativePath).Split('/').Select(Uri.EscapeDataString));
        return new Uri(new Uri(baseUri, UriKind.Absolute), path);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/');
    }

    private static long? ReadInt64Header(HttpResponseMessage response, string headerName)
    {
        return response.Headers.TryGetValues(headerName, out var values)
            && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int NormalizeMaxConnectionsPerNode(ClusterOptions options)
    {
        return Math.Clamp(options.ShardRpcMaxConnectionsPerNode, 1, 1024);
    }

    private static int NormalizeTimeoutSeconds(ClusterOptions options)
    {
        return Math.Clamp(options.ShardRpcRequestTimeoutSeconds, 5, 86_400);
    }

    private static int NormalizePooledConnectionLifetimeSeconds(ClusterOptions options)
    {
        return Math.Clamp(options.ShardRpcPooledConnectionLifetimeSeconds, 30, 86_400);
    }

    private static async Task<MeansException> ToTransportExceptionAsync(
        HttpResponseMessage response,
        ClusterNodeInfo node,
        CancellationToken cancellationToken)
    {
        var body = response.Content.Headers.ContentLength is > 0 and < 8192
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : string.Empty;
        var statusCode = (int)response.StatusCode;
        var message = string.IsNullOrWhiteSpace(body)
            ? $"Cluster shard transfer to {node.NodeId} failed with HTTP {statusCode}."
            : body;
        return new MeansException(MeansErrorCodes.SlowDown, message, statusCode);
    }
}
