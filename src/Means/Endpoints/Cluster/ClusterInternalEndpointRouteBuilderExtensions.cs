using System.Security.Cryptography;
using System.Text;
using Means.Configuration;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Endpoints.Cluster;

public static class ClusterInternalEndpointRouteBuilderExtensions
{
    private const string ClusterTokenHeader = "x-means-cluster-token";
    private const string ShardLengthHeader = "x-means-shard-length";
    private const string ShardChecksumHeader = "x-means-shard-sha256";

    public static IEndpointRouteBuilder MapMeansClusterDataPlane(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/internal/cluster");
        group.MapPut("/shards/{diskId}/{**relativePath}", PutShardAsync);
        group.MapGet("/shards/{diskId}/{**relativePath}", GetShardAsync);
        group.MapMethods("/shards/{diskId}/{**relativePath}", [HttpMethods.Head], HeadShardAsync);
        group.MapDelete("/shards/{diskId}/{**relativePath}", DeleteShardAsync);
        group.MapPut("/manifests/{diskId}/{**relativePath}", PutManifestAsync);
        group.MapGet("/manifests/{diskId}/{**relativePath}", GetManifestAsync);
        group.MapMethods("/manifests/{diskId}/{**relativePath}", [HttpMethods.Head], HeadManifestAsync);
        group.MapDelete("/manifests/{diskId}/{**relativePath}", DeleteManifestAsync);
        return endpoints;
    }

    private static async Task PutShardAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        var maxBytes = Math.Max(1, options.Value.MaxShardTransferBytes);
        if (context.Request.ContentLength is not null && context.Request.ContentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        try
        {
            var expectedLength = ReadExpectedLength(context);
            var result = await store.WriteShardAsync(
                diskId,
                relativePath,
                context.Request.Body,
                expectedLength ?? context.Request.ContentLength,
                context.Request.Headers[ShardChecksumHeader].FirstOrDefault(),
                maxBytes,
                cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers[ShardLengthHeader] = result.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers[ShardChecksumHeader] = result.ChecksumSha256;
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task GetShardAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            await using var shard = await store.OpenShardAsync(diskId, relativePath, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = shard.Length;
            context.Response.Headers[ShardLengthHeader] = shard.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(shard.ContentPath))
            {
                await context.Response.SendFileAsync(shard.ContentPath, 0, shard.Length, cancellationToken);
                return;
            }

            await shard.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task DeleteShardAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            var deleted = await store.DeleteShardAsync(diskId, relativePath, cancellationToken);
            context.Response.StatusCode = deleted ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task HeadShardAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            var stat = await store.StatShardAsync(diskId, relativePath, cancellationToken);
            WriteStatHeaders(context, stat);
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task PutManifestAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        var maxBytes = Math.Max(1, options.Value.MaxShardTransferBytes);
        if (context.Request.ContentLength is not null && context.Request.ContentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        try
        {
            var expectedLength = ReadExpectedLength(context);
            var result = await store.WriteManifestAsync(
                diskId,
                relativePath,
                context.Request.Body,
                expectedLength ?? context.Request.ContentLength,
                context.Request.Headers[ShardChecksumHeader].FirstOrDefault(),
                maxBytes,
                cancellationToken);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers[ShardLengthHeader] = result.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers[ShardChecksumHeader] = result.ChecksumSha256;
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task GetManifestAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            await using var manifest = await store.OpenManifestAsync(diskId, relativePath, cancellationToken);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = manifest.Length;
            context.Response.Headers[ShardLengthHeader] = manifest.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(manifest.ContentPath))
            {
                await context.Response.SendFileAsync(manifest.ContentPath, 0, manifest.Length, cancellationToken);
                return;
            }

            await manifest.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task DeleteManifestAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            var deleted = await store.DeleteManifestAsync(diskId, relativePath, cancellationToken);
            context.Response.StatusCode = deleted ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static async Task HeadManifestAsync(
        HttpContext context,
        string diskId,
        string relativePath,
        IClusterShardStore store,
        IOptions<ClusterOptions> options,
        CancellationToken cancellationToken)
    {
        if (!Authorize(context, options.Value))
        {
            return;
        }

        try
        {
            var stat = await store.StatManifestAsync(diskId, relativePath, cancellationToken);
            WriteStatHeaders(context, stat);
        }
        catch (MeansException ex)
        {
            await WriteInternalErrorAsync(context, ex, cancellationToken);
        }
    }

    private static void WriteStatHeaders(HttpContext context, ClusterShardStatResult stat)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = stat.Length;
        context.Response.Headers[ShardLengthHeader] = stat.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers[ShardChecksumHeader] = stat.ChecksumSha256;
    }

    private static bool Authorize(HttpContext context, ClusterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.InternalAuthToken))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        var supplied = context.Request.Headers[ClusterTokenHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied) || !FixedTimeEquals(supplied, options.InternalAuthToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static long? ReadExpectedLength(HttpContext context)
    {
        var value = context.Request.Headers[ShardLengthHeader].FirstOrDefault();
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static async Task WriteInternalErrorAsync(
        HttpContext context,
        MeansException exception,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = exception.StatusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(exception.Message, cancellationToken);
    }
}
