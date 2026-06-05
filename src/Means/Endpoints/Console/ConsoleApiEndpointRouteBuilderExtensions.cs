using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Means.Configuration;
using Means.Core;
using Means.Infrastructure.XlFs;
using Means.Protocol.S3;
using Means.Security;
using Means.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Means.Endpoints.Console;

/// <summary>
/// JSON management API consumed by the built-in React console.
/// The console API is deliberately cookie-authenticated so browser code never handles S3 secrets.
/// </summary>
public static class ConsoleApiEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapConsoleApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/console");
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous();

        var authenticated = api.MapGroup("").RequireAuthorization();
        authenticated.MapPost("/auth/logout", LogoutAsync);
        authenticated.MapGet("/auth/session", Session);
        authenticated.MapGet("/overview", OverviewAsync);
        authenticated.MapGet("/dashboard-stats", DashboardStatsAsync);
        authenticated.MapGet("/cluster", ClusterAsync);
        authenticated.MapGet("/diagnostics", DiagnosticsAsync);
        authenticated.MapGet("/background-tasks", BackgroundTasksAsync);
        authenticated.MapPost("/background-tasks/{taskId}/run", RunBackgroundTaskAsync);
        authenticated.MapGet("/ec-profiles", ListErasureCodingProfilesAsync);
        authenticated.MapPut("/ec-profiles/{profileId}", SaveErasureCodingProfileAsync);
        authenticated.MapDelete("/ec-profiles/{profileId}", DeleteErasureCodingProfileAsync);
        authenticated.MapGet("/buckets", BucketsAsync);
        authenticated.MapPost("/buckets", CreateBucketAsync);
        authenticated.MapDelete("/buckets/{bucketName}", DeleteBucketAsync);
        authenticated.MapGet("/buckets/{bucketName}/summary", BucketSummaryAsync);
        authenticated.MapGet("/buckets/{bucketName}/settings", BucketSettingsAsync);
        authenticated.MapPut("/buckets/{bucketName}/settings", UpdateBucketSettingsAsync);
        authenticated.MapGet("/buckets/{bucketName}/versioning", GetBucketVersioningAsync);
        authenticated.MapPut("/buckets/{bucketName}/versioning", PutBucketVersioningAsync);
        authenticated.MapGet("/buckets/{bucketName}/lifecycle", GetBucketLifecycleAsync);
        authenticated.MapPut("/buckets/{bucketName}/lifecycle", PutBucketLifecycleAsync);
        authenticated.MapDelete("/buckets/{bucketName}/lifecycle", DeleteBucketLifecycleAsync);
        authenticated.MapGet("/buckets/{bucketName}/policy", GetPolicyAsync);
        authenticated.MapPut("/buckets/{bucketName}/policy", PutPolicyAsync);
        authenticated.MapDelete("/buckets/{bucketName}/policy", DeletePolicyAsync);
        authenticated.MapGet("/buckets/{bucketName}/objects", ListObjectsAsync);
        authenticated.MapGet("/buckets/{bucketName}/objects/versions", ListObjectVersionsAsync);
        authenticated.MapGet("/buckets/{bucketName}/objects/detail", HeadObjectAsync);
        authenticated.MapDelete("/buckets/{bucketName}/objects", DeleteObjectAsync);
        authenticated.MapPost("/buckets/{bucketName}/objects/copy", CopyObjectAsync);
        authenticated.MapPost("/buckets/{bucketName}/objects/presign-upload", PresignUpload);
        authenticated.MapPost("/buckets/{bucketName}/objects/presign-download", PresignDownload);
        authenticated.MapPost("/buckets/{bucketName}/objects/multipart/initiate", InitiateMultipartUploadAsync);
        authenticated.MapPost("/buckets/{bucketName}/objects/multipart/presign-part", PresignMultipartPart);
        authenticated.MapPost("/buckets/{bucketName}/objects/multipart/complete", CompleteMultipartUploadAsync);
        authenticated.MapPost("/buckets/{bucketName}/objects/multipart/abort", AbortMultipartUploadAsync);
        authenticated.MapGet("/access-keys", AccessKeysAsync);
        authenticated.MapPost("/access-keys", CreateAccessKeyAsync);
        authenticated.MapDelete("/access-keys/{accessKey}", DeleteAccessKeyAsync);
        authenticated.MapGet("/settings", SettingsAsync);
        authenticated.MapPut("/settings", UpdateSettingsAsync);
        authenticated.MapGet("/audit", AuditAsync);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        LoginRequest request,
        IConsoleStore consoleStore,
        IOptions<ConsoleOptions> options,
        CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!string.Equals(request.UserName, configured.AdminUser, StringComparison.Ordinal)
            || request.Password != configured.AdminPassword)
        {
            await AppendAuditAsync(consoleStore, request.UserName, "auth.login", "console", "denied", "Invalid credentials.", cancellationToken);
            return Results.Unauthorized();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, configured.AdminUser),
            new Claim(ClaimTypes.Role, "ConsoleAdmin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Clamp(configured.SessionHours, 1, 24))
            });

        await AppendAuditAsync(consoleStore, configured.AdminUser, "auth.login", "console", "success", null, cancellationToken);
        return Results.Ok(new SessionResponse(true, configured.AdminUser));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IConsoleStore consoleStore, CancellationToken cancellationToken)
    {
        var actor = Actor(context);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await AppendAuditAsync(consoleStore, actor, "auth.logout", "console", "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static IResult Session(HttpContext context)
    {
        return Results.Ok(new SessionResponse(true, Actor(context)));
    }

    private static async Task<IResult> OverviewAsync(
        HttpContext context,
        IConsoleStore consoleStore,
        IOptions<XlFsOptions> storageOptions,
        IOptions<S3AddressingOptions> addressingOptions,
        CancellationToken cancellationToken)
    {
        var metrics = await consoleStore.GetStorageMetricsAsync(cancellationToken);
        var buckets = await consoleStore.ListBucketUsageAsync(cancellationToken);
        var storage = storageOptions.Value;
        var addressing = addressingOptions.Value;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        await AppendAuditAsync(consoleStore, Actor(context), "overview.read", "console", "success", null, cancellationToken);
        return Results.Ok(new OverviewResponse(
            metrics.BucketCount,
            metrics.ObjectCount,
            metrics.TotalBytes,
            ResolveMetadataPath(storage),
            ResolvePath(storage.ObjectsPath),
            addressing.ServiceHost,
            addressing.DomainSuffix,
            addressing.AliasPrefix,
            version,
            buckets.Take(5).ToArray()));
    }

    private static async Task<IResult> DashboardStatsAsync(
        int? hours,
        IConsoleStore consoleStore,
        IClusterStore clusterStore,
        IOptions<XlFsOptions> storageOptions,
        IOptions<S3AddressingOptions> addressingOptions,
        IOptions<ClusterOptions> clusterOptions,
        CancellationToken cancellationToken)
    {
        var requestedHours = Math.Clamp(hours ?? 24, 1, 168);
        var endUtc = TruncateToHour(DateTimeOffset.UtcNow).AddHours(1);
        var startUtc = endUtc.AddHours(-requestedHours);

        var metrics = await consoleStore.GetStorageMetricsAsync(cancellationToken);
        var bucketUsage = await consoleStore.ListBucketUsageAsync(cancellationToken);
        var hourlyMetrics = await consoleStore.ListHourlyMetricsAsync(startUtc, endUtc, cancellationToken);
        var bucketActivity = await consoleStore.ListBucketActivityAsync(startUtc, endUtc, 5, cancellationToken);

        var storage = storageOptions.Value;
        var addressing = addressingOptions.Value;
        var metadataPath = ResolveMetadataPath(storage);
        var objectsPath = ResolvePath(storage.ObjectsPath);
        var capacity = GetCapacity(objectsPath, metrics.TotalBytes);
        var pathStatuses = new[]
        {
            GetPathStatus("LogDb Metadata", metadataPath, isFilePath: false),
            GetPathStatus("Object Blobs", objectsPath, isFilePath: false)
        };
        var onlineDrives = pathStatuses.Count(path => path.Online);
        var topology = await clusterStore.GetClusterTopologyAsync(ClusterOfflineBefore(clusterOptions.Value), cancellationToken);
        var onlineNodes = topology.Nodes.Count(node => node.Status == ClusterNodeStatuses.Online);
        var offlineNodes = topology.Nodes.Count - onlineNodes;
        var clusterDisks = topology.Nodes.SelectMany(node => node.Disks).ToArray();
        var clusterOnlineDrives = clusterDisks.Count(disk => disk.Status == StorageDiskStatuses.Online);
        var clusterOfflineDrives = clusterDisks.Length - clusterOnlineDrives;
        var poolResponses = BuildDashboardPools(topology, capacity, metrics.TotalBytes, pathStatuses.Length, onlineDrives);
        var paddedHourly = PadHourlyMetrics(startUtc, requestedHours, hourlyMetrics);
        var usageByBucket = bucketUsage.ToDictionary(bucket => bucket.BucketName, StringComparer.Ordinal);
        var recentBuckets = BuildRecentBuckets(bucketActivity, bucketUsage, usageByBucket);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        return Results.Ok(new DashboardStatsResponse(
            new DashboardRangeResponse(requestedHours, startUtc, endUtc),
            capacity,
            new DashboardSummaryResponse(
                metrics.BucketCount,
                metrics.ObjectCount,
                metrics.TotalBytes,
                paddedHourly.Sum(point => point.RequestCount),
                paddedHourly.Sum(point => point.ErrorCount),
                version),
            new DashboardNodesResponse(
                ServersOnline: topology.Nodes.Count > 0 ? onlineNodes : 1,
                ServersOffline: topology.Nodes.Count > 0 ? offlineNodes : 0,
                DrivesOnline: clusterDisks.Length > 0 ? clusterOnlineDrives : onlineDrives,
                DrivesOffline: clusterDisks.Length > 0 ? clusterOfflineDrives : pathStatuses.Length - onlineDrives,
                MetadataPath: metadataPath,
                ObjectsPath: objectsPath,
                ServiceHost: addressing.ServiceHost,
                DomainSuffix: addressing.DomainSuffix,
                AliasPrefix: addressing.AliasPrefix,
                Paths: pathStatuses),
            paddedHourly,
            recentBuckets,
            poolResponses));
    }

    private static async Task<IResult> ClusterAsync(
        HttpContext context,
        IClusterStore clusterStore,
        IConsoleStore consoleStore,
        IOptions<ClusterOptions> clusterOptions,
        CancellationToken cancellationToken)
    {
        var topology = await clusterStore.GetClusterTopologyAsync(ClusterOfflineBefore(clusterOptions.Value), cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "cluster.read", topology.Cluster.ClusterId, "success", null, cancellationToken);
        return Results.Ok(topology);
    }

    private static async Task<IResult> DiagnosticsAsync(
        HttpContext context,
        IConsoleStore consoleStore,
        IBackgroundTaskManager backgroundTasks,
        IOptions<ClusterOptions> clusterOptions,
        CancellationToken cancellationToken)
    {
        var configuredCluster = clusterOptions.Value;
        var diagnostics = await consoleStore.GetClusterDiagnosticsAsync(ClusterOfflineBefore(configuredCluster), cancellationToken);
        diagnostics = diagnostics with
        {
            InternalTransport = new ClusterInternalTransportDiagnostics(
                !string.IsNullOrWhiteSpace(configuredCluster.InternalAuthToken),
                Math.Max(0, configuredCluster.MaxShardTransferBytes),
                Math.Clamp(configuredCluster.ShardRpcMaxConnectionsPerNode, 1, 1024),
                Math.Clamp(configuredCluster.ShardRpcRequestTimeoutSeconds, 5, 86_400),
                Math.Clamp(configuredCluster.ShardRpcPooledConnectionLifetimeSeconds, 30, 86_400)),
            BackgroundTasks = backgroundTasks.ListTasks()
        };
        await AppendAuditAsync(
            consoleStore,
            Actor(context),
            "diagnostics.read",
            diagnostics.Topology.Cluster.ClusterId,
            "success",
            null,
            cancellationToken);
        return Results.Ok(diagnostics);
    }

    private static async Task<IResult> BackgroundTasksAsync(
        HttpContext context,
        IBackgroundTaskManager backgroundTasks,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var response = new BackgroundTaskManagementResponse(
            backgroundTasks.ListGroups(),
            backgroundTasks.ListTasks(),
            backgroundTasks.ListHistory(taskId: null, limit: 50));
        await AppendAuditAsync(consoleStore, Actor(context), "background-task.read", "background-tasks", "success", null, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> RunBackgroundTaskAsync(
        HttpContext context,
        string taskId,
        IBackgroundTaskManager backgroundTasks,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await backgroundTasks.RunTaskAsync(taskId, cancellationToken);
            await AppendAuditAsync(
                consoleStore,
                Actor(context),
                "background-task.run",
                snapshot.TaskId,
                snapshot.Status == BackgroundTaskStatuses.Succeeded ? "success" : snapshot.Status.ToLowerInvariant(),
                snapshot.LastResult ?? snapshot.LastError,
                cancellationToken);
            return Results.Ok(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AppendAuditAsync(consoleStore, Actor(context), "background-task.run", taskId, "failed", ex.Message, cancellationToken);
            throw;
        }
    }

    private static async Task<IResult> ListErasureCodingProfilesAsync(
        IErasureCodingProfileStore profiles,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await profiles.ListErasureCodingProfilesAsync(cancellationToken));
    }

    private static async Task<IResult> SaveErasureCodingProfileAsync(
        HttpContext context,
        string profileId,
        SaveErasureCodingProfileRequest request,
        IErasureCodingProfileStore profiles,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var saved = await profiles.SaveErasureCodingProfileAsync(
            new ErasureCodingProfile(
                profileId,
                request.DataShards,
                request.ParityShards,
                request.CellSizeBytes,
                request.Enabled,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue),
            cancellationToken);
        await AppendAuditAsync(
            consoleStore,
            Actor(context),
            "ec-profile.save",
            saved.ProfileId,
            "success",
            $"Data={saved.DataShards}; Parity={saved.ParityShards}; Cell={saved.CellSizeBytes}; Enabled={saved.Enabled}.",
            cancellationToken);
        return Results.Ok(saved);
    }

    private static async Task<IResult> DeleteErasureCodingProfileAsync(
        HttpContext context,
        string profileId,
        IErasureCodingProfileStore profiles,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        await profiles.DeleteErasureCodingProfileAsync(profileId, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "ec-profile.delete", profileId, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> BucketsAsync(IConsoleStore consoleStore, CancellationToken cancellationToken)
    {
        return Results.Ok(await consoleStore.ListBucketUsageAsync(cancellationToken));
    }

    private static async Task<IResult> CreateBucketAsync(
        HttpContext context,
        CreateBucketRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(request.BucketName);
        var bucket = await store.CreateBucketAsync(request.BucketName, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "bucket.create", request.BucketName, "success", null, cancellationToken);
        return Results.Created(
            $"/api/console/buckets/{request.BucketName}",
            new BucketUsageInfo(bucket.Name, bucket.CreatedAt, ObjectCount: 0, TotalBytes: 0));
    }

    private static async Task<IResult> DeleteBucketAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        await store.DeleteBucketAsync(bucketName, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "bucket.delete", bucketName, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> BucketSummaryAsync(
        string bucketName,
        int? hours,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var requestedHours = Math.Clamp(hours ?? 24, 1, 168);
        var endUtc = TruncateToHour(DateTimeOffset.UtcNow).AddHours(1);
        var startUtc = endUtc.AddHours(-requestedHours);
        var summary = await consoleStore.GetBucketSummaryAsync(bucketName, startUtc, endUtc, cancellationToken);
        return Results.Ok(new BucketSummaryResponse(requestedHours, startUtc, endUtc, summary));
    }

    private static async Task<IResult> BucketSettingsAsync(
        string bucketName,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await consoleStore.GetBucketSettingsAsync(bucketName, cancellationToken));
    }

    private static async Task<IResult> UpdateBucketSettingsAsync(
        HttpContext context,
        string bucketName,
        UpdateBucketSettingsRequest request,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var settings = NormalizeBucketSettings(bucketName, request);
        await consoleStore.SaveBucketSettingsAsync(settings, cancellationToken);
        await AppendAuditAsync(
            consoleStore,
            Actor(context),
            "bucket.settings.update",
            bucketName,
            "success",
            $"Default response headers: {settings.DefaultResponseHeaders.Count}; default metadata: {settings.DefaultMetadata.Count}.",
            cancellationToken);
        return Results.Ok(settings);
    }

    private static async Task<IResult> GetBucketVersioningAsync(
        string bucketName,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        return Results.Ok(await store.GetBucketVersioningAsync(bucketName, cancellationToken));
    }

    private static async Task<IResult> PutBucketVersioningAsync(
        HttpContext context,
        string bucketName,
        BucketVersioningConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        var result = await store.PutBucketVersioningAsync(bucketName, request.Status, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "bucket.versioning.put", bucketName, "success", result.Status, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetBucketLifecycleAsync(
        string bucketName,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        var lifecycle = await store.GetBucketLifecycleAsync(bucketName, cancellationToken)
            ?? new BucketLifecycleConfiguration([]);
        return Results.Ok(lifecycle);
    }

    private static async Task<IResult> PutBucketLifecycleAsync(
        HttpContext context,
        string bucketName,
        BucketLifecycleConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        if (request.Rules is null)
        {
            throw new MeansException(MeansErrorCodes.MalformedXML, "Lifecycle rules are required.", 400);
        }

        var configuration = new BucketLifecycleConfiguration(
            request.Rules.Select(rule => new LifecycleRule(
                rule.Id,
                rule.Status,
                rule.Prefix ?? "",
                rule.ExpirationDays,
                rule.NoncurrentVersionExpirationDays,
                rule.AbortIncompleteMultipartUploadDays)).ToArray());
        await store.PutBucketLifecycleAsync(bucketName, configuration, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "bucket.lifecycle.put", bucketName, "success", $"{configuration.Rules.Count} rule(s).", cancellationToken);
        return Results.Ok(configuration);
    }

    private static async Task<IResult> DeleteBucketLifecycleAsync(
        HttpContext context,
        string bucketName,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        await store.DeleteBucketLifecycleAsync(bucketName, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "bucket.lifecycle.delete", bucketName, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPolicyAsync(string bucketName, IBucketPolicyRepository policies, CancellationToken cancellationToken)
    {
        return Results.Ok(new PolicyResponse(await policies.GetPolicyAsync(bucketName, cancellationToken) ?? ""));
    }

    private static async Task<IResult> PutPolicyAsync(
        HttpContext context,
        string bucketName,
        PolicyRequest request,
        IBucketPolicyRepository policies,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        using var _ = JsonDocument.Parse(request.Policy);
        await policies.PutPolicyAsync(bucketName, request.Policy, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "policy.put", bucketName, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeletePolicyAsync(
        HttpContext context,
        string bucketName,
        IBucketPolicyRepository policies,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        await policies.DeletePolicyAsync(bucketName, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "policy.delete", bucketName, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListObjectsAsync(
        string bucketName,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        int? maxKeys,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.ListObjectsAsync(
            bucketName,
            new ListObjectsOptions(prefix, delimiter, continuationToken, maxKeys ?? 1000),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ListObjectVersionsAsync(
        string bucketName,
        string? prefix,
        string? delimiter,
        string? keyMarker,
        string? versionIdMarker,
        int? maxKeys,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        var result = await store.ListObjectVersionsAsync(
            bucketName,
            new ListObjectVersionsOptions(prefix, delimiter, keyMarker, versionIdMarker, maxKeys ?? 1000),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> HeadObjectAsync(string bucketName, string key, string? versionId, IObjectStore store, CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateObjectKey(key);
        return Results.Ok(await store.HeadObjectAsync(bucketName, key, versionId, cancellationToken));
    }

    private static async Task<IResult> DeleteObjectAsync(
        HttpContext context,
        string bucketName,
        string key,
        string? versionId,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateObjectKey(key);
        var deleted = await store.DeleteObjectAsync(bucketName, key, versionId, cancellationToken);
        await AppendAuditAsync(
            consoleStore,
            Actor(context),
            "object.delete",
            $"{bucketName}/{key}",
            "success",
            deleted.VersionId is null ? null : $"VersionId={deleted.VersionId}; DeleteMarker={deleted.DeleteMarker}.",
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CopyObjectAsync(
        HttpContext context,
        string bucketName,
        CopyObjectConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateObjectKey(request.DestinationKey);
        var copied = await store.CopyObjectAsync(
            new CopyObjectRequest(
                request.SourceBucket,
                request.SourceKey,
                SourceVersionId: null,
                bucketName,
                request.DestinationKey,
                new Dictionary<string, string>(),
                CopyMetadataDirectives.Copy,
                ContentType: null,
                CacheControl: null,
                ContentDisposition: null),
            cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "object.copy", $"{bucketName}/{request.DestinationKey}", "success", null, cancellationToken);
        return Results.Ok(copied);
    }

    private static async Task<IResult> PresignUpload(
        HttpContext context,
        string bucketName,
        PresignRequest request,
        IOptions<XlFsOptions> storageOptions,
        SystemSettingsService settings,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var response = CreatePresignedResponse(
            context,
            bucketName,
            request.Key,
            HttpMethod.Put,
            request.ExpiresSeconds,
            storageOptions.Value,
            await settings.GetAsync(cancellationToken));
        await AppendAuditAsync(consoleStore, Actor(context), "object.presign-upload", $"{bucketName}/{request.Key}", "success", null, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> InitiateMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        InitiateMultipartConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        S3NameValidator.ValidateObjectKey(request.Key);
        var upload = await store.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest(
                bucketName,
                request.Key,
                string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
                request.Metadata ?? new Dictionary<string, string>(),
                request.CacheControl,
                request.ContentDisposition),
            cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "object.multipart.initiate", $"{bucketName}/{request.Key}", "success", upload.UploadId, cancellationToken);
        return Results.Ok(new MultipartUploadConsoleResponse(upload.BucketName, upload.Key, upload.UploadId));
    }

    private static async Task<IResult> PresignMultipartPart(
        HttpContext context,
        string bucketName,
        PresignMultipartPartRequest request,
        IOptions<XlFsOptions> storageOptions,
        SystemSettingsService settings,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        S3NameValidator.ValidateObjectKey(request.Key);
        if (request.PartNumber is < 1 or > 10000)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Part number must be between 1 and 10000.", 400);
        }

        var response = CreatePresignedResponse(
            context,
            bucketName,
            request.Key,
            HttpMethod.Put,
            request.ExpiresSeconds,
            storageOptions.Value,
            await settings.GetAsync(cancellationToken),
            new Dictionary<string, string>
            {
                ["partNumber"] = request.PartNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["uploadId"] = request.UploadId
            });
        await AppendAuditAsync(consoleStore, Actor(context), "object.multipart.presign-part", $"{bucketName}/{request.Key}", "success", $"UploadId={request.UploadId}; Part={request.PartNumber}.", cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> CompleteMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        CompleteMultipartConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        S3NameValidator.ValidateObjectKey(request.Key);
        var completed = await store.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest(
                bucketName,
                request.Key,
                request.UploadId,
                request.Parts.Select(part => new CompletedMultipartPart(part.PartNumber, part.ETag)).ToArray()),
            cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "object.multipart.complete", $"{bucketName}/{request.Key}", "success", request.UploadId, cancellationToken);
        return Results.Ok(completed);
    }

    private static async Task<IResult> AbortMultipartUploadAsync(
        HttpContext context,
        string bucketName,
        AbortMultipartConsoleRequest request,
        IObjectStore store,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        S3NameValidator.ValidateObjectKey(request.Key);
        await store.AbortMultipartUploadAsync(bucketName, request.Key, request.UploadId, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "object.multipart.abort", $"{bucketName}/{request.Key}", "success", request.UploadId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PresignDownload(
        HttpContext context,
        string bucketName,
        PresignRequest request,
        IOptions<XlFsOptions> storageOptions,
        SystemSettingsService settings,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(request.VersionId)
            ? null
            : new Dictionary<string, string> { ["versionId"] = request.VersionId };
        var response = CreatePresignedResponse(
            context,
            bucketName,
            request.Key,
            HttpMethod.Get,
            request.ExpiresSeconds,
            storageOptions.Value,
            await settings.GetAsync(cancellationToken),
            query);
        await AppendAuditAsync(consoleStore, Actor(context), "object.presign-download", $"{bucketName}/{request.Key}", "success", null, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> AccessKeysAsync(IConsoleStore consoleStore, CancellationToken cancellationToken)
    {
        return Results.Ok(await consoleStore.ListAccessKeysAsync(cancellationToken));
    }

    private static async Task<IResult> CreateAccessKeyAsync(
        HttpContext context,
        CreateAccessKeyRequest request,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var created = await consoleStore.CreateAccessKeyAsync(request.AccessKey, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "access-key.create", created.AccessKey, "success", "Secret was returned once.", cancellationToken);
        return Results.Created($"/api/console/access-keys/{created.AccessKey}", created);
    }

    private static async Task<IResult> DeleteAccessKeyAsync(
        HttpContext context,
        string accessKey,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        await consoleStore.DeleteAccessKeyAsync(accessKey, cancellationToken);
        await AppendAuditAsync(consoleStore, Actor(context), "access-key.delete", accessKey, "success", null, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AuditAsync(int? limit, IConsoleStore consoleStore, CancellationToken cancellationToken)
    {
        return Results.Ok(await consoleStore.ListAuditAsync(limit ?? 100, cancellationToken));
    }

    private static async Task<IResult> SettingsAsync(
        SystemSettingsService settings,
        CancellationToken cancellationToken)
    {
        return Results.Ok(SystemSettingsResponse.From(await settings.GetAsync(cancellationToken)));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        HttpContext context,
        UpdateSystemSettingsRequest request,
        SystemSettingsService settings,
        IConsoleStore consoleStore,
        CancellationToken cancellationToken)
    {
        var updated = await settings.SaveAsync(new SystemSettings(request.MaxUploadSizeBytes, request.PublicOrigin), cancellationToken);
        await AppendAuditAsync(
            consoleStore,
            Actor(context),
            "settings.update",
            "system",
            "success",
            $"Max upload size set to {updated.MaxUploadSizeBytes} bytes. Public origin: {updated.PublicOrigin ?? "(auto)"}.",
            cancellationToken);
        return Results.Ok(SystemSettingsResponse.From(updated));
    }

    private static PresignedTransferResponse CreatePresignedResponse(
        HttpContext context,
        string bucketName,
        string key,
        HttpMethod method,
        int? requestedExpiresSeconds,
        XlFsOptions storageOptions,
        SystemSettings settings,
        IReadOnlyDictionary<string, string>? query = null)
    {
        S3NameValidator.ValidateBucketName(bucketName);
        S3NameValidator.ValidateObjectKey(key);
        var expiresSeconds = Math.Clamp(requestedExpiresSeconds ?? 900, 60, 604800);
        var origin = settings.PublicOrigin ?? $"{context.Request.Scheme}://{context.Request.Host}";
        var builder = new UriBuilder($"{origin}/s3/{Uri.EscapeDataString(bucketName)}/{EscapeKey(key)}");
        if (query is not null && query.Count > 0)
        {
            builder.Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        var signed = SigV4RequestSigner.Presign(
            builder.Uri,
            method,
            new SigV4SigningCredentials(storageOptions.DefaultAccessKey, storageOptions.DefaultSecretKey),
            TimeSpan.FromSeconds(expiresSeconds));
        var url = settings.PublicOrigin is null ? signed.PathAndQuery : signed.AbsoluteUri;
        return new PresignedTransferResponse(method.Method, url, expiresSeconds);
    }

    private static async Task AppendAuditAsync(
        IConsoleStore consoleStore,
        string actor,
        string action,
        string resource,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        await consoleStore.AppendAuditAsync(
            new AuditEntry(0, DateTimeOffset.UtcNow, actor, action, resource, status, message),
            cancellationToken);
    }

    private static string Actor(HttpContext context)
    {
        return context.User.Identity?.Name ?? "anonymous";
    }

    private static string EscapeKey(string key)
    {
        return string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string ResolveMetadataPath(XlFsOptions storage)
    {
        var root = storage.Disks.Length > 0 ? storage.Disks[0] : storage.ObjectsPath;
        return Path.Combine(ResolvePath(root), ".means.sys", "meta");
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static DashboardCapacityResponse GetCapacity(string objectsPath, long objectBytes)
    {
        try
        {
            var root = Path.GetPathRoot(objectsPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = objectsPath;
            }

            var drive = new DriveInfo(root);
            var totalBytes = Math.Max(0, drive.TotalSize);
            var freeBytes = Math.Max(0, drive.AvailableFreeSpace);
            var usedBytes = Math.Max(0, totalBytes - freeBytes);
            return new DashboardCapacityResponse(
                totalBytes,
                usedBytes,
                freeBytes,
                objectBytes,
                totalBytes == 0 ? 0 : Math.Round(usedBytes * 100d / totalBytes, 2));
        }
        catch
        {
            return new DashboardCapacityResponse(
                TotalBytes: objectBytes,
                UsedBytes: objectBytes,
                FreeBytes: 0,
                ObjectBytes: objectBytes,
                UsedPercent: objectBytes > 0 ? 100 : 0);
        }
    }

    private static DateTimeOffset ClusterOfflineBefore(ClusterOptions options)
    {
        return DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(Math.Max(5, options.OfflineAfterSeconds)));
    }

    private static IReadOnlyList<DashboardPoolResponse> BuildDashboardPools(
        ClusterTopology topology,
        DashboardCapacityResponse fallbackCapacity,
        long objectBytes,
        int fallbackDriveCount,
        int fallbackOnlineDrives)
    {
        if (topology.Pools.Count == 0)
        {
            return
            [
                new DashboardPoolResponse(
                    "Pool 1",
                    fallbackCapacity.TotalBytes,
                    fallbackCapacity.UsedBytes,
                    fallbackCapacity.FreeBytes,
                    fallbackCapacity.ObjectBytes,
                    fallbackDriveCount,
                    fallbackOnlineDrives,
                    fallbackDriveCount - fallbackOnlineDrives)
            ];
        }

        var disksByPool = topology.Nodes
            .SelectMany(node => node.Disks)
            .GroupBy(disk => disk.PoolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var singlePoolObjectBytes = topology.Pools.Count == 1 ? objectBytes : 0;
        return topology.Pools.Select(pool =>
        {
            disksByPool.TryGetValue(pool.PoolId, out var disks);
            disks ??= [];
            var onlineDrives = disks.Count(disk => disk.Status == StorageDiskStatuses.Online);
            var totalBytes = Math.Max(0, pool.TotalBytes);
            var freeBytes = Math.Max(0, pool.AvailableBytes);
            return new DashboardPoolResponse(
                pool.Name,
                totalBytes,
                Math.Max(0, totalBytes - freeBytes),
                freeBytes,
                singlePoolObjectBytes,
                pool.DiskCount,
                onlineDrives,
                pool.DiskCount - onlineDrives);
        }).ToArray();
    }

    private static DashboardPathStatusResponse GetPathStatus(string name, string path, bool isFilePath)
    {
        try
        {
            var directory = isFilePath ? Path.GetDirectoryName(path) : path;
            var online = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
            return new DashboardPathStatusResponse(name, path, online);
        }
        catch
        {
            return new DashboardPathStatusResponse(name, path, Online: false);
        }
    }

    private static IReadOnlyList<DashboardHourlyMetricResponse> PadHourlyMetrics(
        DateTimeOffset startUtc,
        int hours,
        IReadOnlyList<ConsoleHourlyMetric> metrics)
    {
        var byHour = metrics.ToDictionary(metric => TruncateToHour(metric.HourUtc), metric => metric);
        var hourly = new List<DashboardHourlyMetricResponse>(hours);
        for (var index = 0; index < hours; index++)
        {
            var hour = startUtc.AddHours(index);
            if (byHour.TryGetValue(hour, out var metric))
            {
                hourly.Add(new DashboardHourlyMetricResponse(
                    hour,
                    metric.RequestCount,
                    metric.ErrorCount,
                    metric.IngressBytes,
                    metric.EgressBytes,
                    metric.PutCount,
                    metric.GetCount,
                    metric.DeleteCount,
                    metric.HeadCount,
                    metric.ListCount));
                continue;
            }

            hourly.Add(new DashboardHourlyMetricResponse(hour, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        return hourly;
    }

    private static IReadOnlyList<DashboardRecentBucketResponse> BuildRecentBuckets(
        IReadOnlyList<ConsoleBucketActivity> activity,
        IReadOnlyList<BucketUsageInfo> buckets,
        IReadOnlyDictionary<string, BucketUsageInfo> usageByBucket)
    {
        if (activity.Count > 0)
        {
            return activity.Select(item =>
            {
                usageByBucket.TryGetValue(item.BucketName, out var usage);
                return new DashboardRecentBucketResponse(
                    item.BucketName,
                    item.RequestCount,
                    item.ErrorCount,
                    item.IngressBytes,
                    item.EgressBytes,
                    usage?.ObjectCount ?? 0,
                    usage?.TotalBytes ?? 0,
                    item.LastActivityAt);
            }).ToArray();
        }

        return buckets
            .OrderByDescending(bucket => bucket.TotalBytes)
            .ThenBy(bucket => bucket.BucketName, StringComparer.Ordinal)
            .Take(5)
            .Select(bucket => new DashboardRecentBucketResponse(
                bucket.BucketName,
                RequestCount: 0,
                ErrorCount: 0,
                IngressBytes: 0,
                EgressBytes: 0,
                bucket.ObjectCount,
                bucket.TotalBytes,
                LastActivityAt: null))
            .ToArray();
    }

    private static BucketSettings NormalizeBucketSettings(string bucketName, UpdateBucketSettingsRequest request)
    {
        return new BucketSettings(
            bucketName,
            NormalizeResponseHeaders(request.DefaultResponseHeaders),
            NormalizeMetadata(request.DefaultMetadata),
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyDictionary<string, string> NormalizeResponseHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in headers ?? new Dictionary<string, string>())
        {
            var name = CanonicalResponseHeaderName(item.Key);
            if (name is null)
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, $"Unsupported default response header '{item.Key}'.", 400);
            }

            var value = NormalizeHeaderValue(item.Value, name);
            if (value is not null)
            {
                normalized[name] = value;
            }
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata ?? new Dictionary<string, string>())
        {
            var name = item.Key.Trim();
            if (name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                name = name["x-amz-meta-".Length..];
            }

            if (!IsSafeMetadataName(name))
            {
                throw new MeansException(MeansErrorCodes.InvalidArgument, $"Invalid metadata name '{item.Key}'.", 400);
            }

            var value = NormalizeHeaderValue(item.Value, "x-amz-meta-" + name);
            if (value is not null)
            {
                normalized[name.ToLowerInvariant()] = value;
            }
        }

        if (normalized.Count > 20)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Bucket default metadata supports at most 20 entries.", 400);
        }

        return normalized;
    }

    private static string? CanonicalResponseHeaderName(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "cache-control" => "Cache-Control",
            "content-disposition" => "Content-Disposition",
            "content-language" => "Content-Language",
            "expires" => "Expires",
            _ => null
        };
    }

    private static string? NormalizeHeaderValue(string value, string fieldName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > 1024 || normalized.Any(character => character is '\r' or '\n'))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, $"Invalid value for {fieldName}.", 400);
        }

        if (string.Equals(fieldName, "Expires", StringComparison.OrdinalIgnoreCase)
            && !DateTimeOffset.TryParse(normalized, out _))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Expires must be a valid HTTP date.", 400);
        }

        return normalized;
    }

    private static bool IsSafeMetadataName(string value)
    {
        if (value.Length is < 1 or > 64)
        {
            return false;
        }

        return value.All(character =>
            character is >= 'a' and <= 'z'
            || character is >= 'A' and <= 'Z'
            || character is >= '0' and <= '9'
            || character is '-' or '_' or '.');
    }
}

public sealed record LoginRequest(string UserName, string Password);

public sealed record SessionResponse(bool Authenticated, string UserName);

public sealed record OverviewResponse(
    long BucketCount,
    long ObjectCount,
    long TotalBytes,
    string MetadataPath,
    string ObjectsPath,
    string ServiceHost,
    string DomainSuffix,
    string AliasPrefix,
    string Version,
    IReadOnlyList<BucketUsageInfo> TopBuckets);

public sealed record DashboardStatsResponse(
    DashboardRangeResponse Range,
    DashboardCapacityResponse Capacity,
    DashboardSummaryResponse Summary,
    DashboardNodesResponse Nodes,
    IReadOnlyList<DashboardHourlyMetricResponse> Hourly,
    IReadOnlyList<DashboardRecentBucketResponse> RecentBuckets,
    IReadOnlyList<DashboardPoolResponse> Pools);

public sealed record BucketSummaryResponse(
    int Hours,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    BucketConsoleSummary Summary);

public sealed record DashboardRangeResponse(int Hours, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record DashboardCapacityResponse(
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    long ObjectBytes,
    double UsedPercent);

public sealed record DashboardSummaryResponse(
    long BucketCount,
    long ObjectCount,
    long TotalBytes,
    long RequestCount,
    long ErrorCount,
    string Version);

public sealed record DashboardNodesResponse(
    int ServersOnline,
    int ServersOffline,
    int DrivesOnline,
    int DrivesOffline,
    string MetadataPath,
    string ObjectsPath,
    string ServiceHost,
    string DomainSuffix,
    string AliasPrefix,
    IReadOnlyList<DashboardPathStatusResponse> Paths);

public sealed record DashboardPathStatusResponse(string Name, string Path, bool Online);

public sealed record DashboardHourlyMetricResponse(
    DateTimeOffset HourUtc,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    long PutCount,
    long GetCount,
    long DeleteCount,
    long HeadCount,
    long ListCount);

public sealed record DashboardRecentBucketResponse(
    string BucketName,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    long ObjectCount,
    long TotalBytes,
    DateTimeOffset? LastActivityAt);

public sealed record DashboardPoolResponse(
    string Name,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    long ObjectBytes,
    int DriveCount,
    int OnlineDrives,
    int OfflineDrives);

public sealed record BackgroundTaskManagementResponse(
    IReadOnlyList<BackgroundTaskGroup> Groups,
    IReadOnlyList<BackgroundTaskSnapshot> Tasks,
    IReadOnlyList<BackgroundTaskRunRecord> History);

public sealed record CreateBucketRequest(string BucketName);

public sealed record UpdateBucketSettingsRequest(
    IReadOnlyDictionary<string, string>? DefaultResponseHeaders,
    IReadOnlyDictionary<string, string>? DefaultMetadata);

public sealed record BucketVersioningConsoleRequest(string Status);

public sealed record BucketLifecycleConsoleRequest(IReadOnlyList<LifecycleRuleConsoleRequest> Rules);

public sealed record LifecycleRuleConsoleRequest(
    string Id,
    string Status,
    string? Prefix,
    int? ExpirationDays,
    int? NoncurrentVersionExpirationDays,
    int? AbortIncompleteMultipartUploadDays);

public sealed record PolicyRequest(string Policy);

public sealed record PolicyResponse(string Policy);

public sealed record CopyObjectConsoleRequest(string SourceBucket, string SourceKey, string DestinationKey);

public sealed record PresignRequest(string Key, int? ExpiresSeconds, string? VersionId = null);

public sealed record PresignedTransferResponse(string Method, string Url, int ExpiresSeconds);

public sealed record InitiateMultipartConsoleRequest(
    string Key,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Metadata,
    string? CacheControl,
    string? ContentDisposition);

public sealed record MultipartUploadConsoleResponse(string BucketName, string Key, string UploadId);

public sealed record PresignMultipartPartRequest(string Key, string UploadId, int PartNumber, int? ExpiresSeconds);

public sealed record CompleteMultipartConsoleRequest(string Key, string UploadId, IReadOnlyList<CompletedMultipartPartConsoleRequest> Parts);

public sealed record CompletedMultipartPartConsoleRequest(int PartNumber, string ETag);

public sealed record AbortMultipartConsoleRequest(string Key, string UploadId);

public sealed record CreateAccessKeyRequest(string? AccessKey);

public sealed record UpdateSystemSettingsRequest(long MaxUploadSizeBytes, string? PublicOrigin = null);

public sealed record SystemSettingsResponse(
    long MaxUploadSizeBytes,
    long MinimumMaxUploadSizeBytes,
    long MaximumMaxUploadSizeBytes,
    string? PublicOrigin)
{
    public static SystemSettingsResponse From(SystemSettings settings)
    {
        return new SystemSettingsResponse(
            settings.MaxUploadSizeBytes,
            SystemSettings.MinimumMaxUploadSizeBytes,
            SystemSettings.MaximumMaxUploadSizeBytes,
            settings.PublicOrigin);
    }
}

public sealed record SaveErasureCodingProfileRequest(
    int DataShards,
    int ParityShards,
    int CellSizeBytes,
    bool Enabled);
