using Means.Core;
using Means.Configuration;
using Means.Infrastructure.SqliteFs;
using Means.Infrastructure.XlFs;
using Means.Protocol.S3;
using Means.Security;
using Means.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Means.Composition;

/// <summary>
/// Dependency-injection registration for the Means data plane.
/// The host project owns wiring decisions, while Core, Protocol, and Infrastructure projects stay
/// independent from ASP.NET Core bootstrapping concerns.
/// </summary>
public static class MeansDataPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddMeansDataPlane(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ValidateConsoleDefaults(configuration, environment);

        // Configuration sections intentionally mirror project boundaries:
        // Means:S3 configures protocol/address behavior, while Means:Storage configures local durability.
        services.Configure<S3AddressingOptions>(configuration.GetSection("Means:S3"));
        services.Configure<SqliteFsOptions>(configuration.GetSection("Means:Storage"));
        services.Configure<XlFsOptions>(configuration.GetSection("Means:Storage"));
        services.Configure<ClusterOptions>(configuration.GetSection("Means:Cluster"));
        services.Configure<ConsoleOptions>(configuration.GetSection("Means:Console"));
        services.Configure<RequestLimitsOptions>(configuration.GetSection("Means:RequestLimits"));
        services.Configure<MeansRateLimitOptions>(configuration.GetSection("Means:RateLimits"));
        services.Configure<TelemetryOptions>(configuration.GetSection("Means:Telemetry"));
        AddMeansOpenTelemetry(services, configuration);

        // Register both adapters, then resolve the selected backend from final bound options.
        // This keeps WebApplicationFactory/test overrides and production config behavior consistent.
        services.AddSingleton<SqliteFsStore>();
        services.AddSingleton<XlFsStore>();
        services.AddSingleton<IObjectStore>(ResolveStore<IObjectStore>);
        services.AddSingleton<IAccessKeyStore>(ResolveStore<IAccessKeyStore>);
        services.AddSingleton<IBucketPolicyRepository>(ResolveStore<IBucketPolicyRepository>);
        services.AddSingleton<IConsoleStore>(ResolveStore<IConsoleStore>);
        services.AddSingleton<IClusterStore>(ResolveStore<IClusterStore>);
        services.AddSingleton<IErasureCodingProfileStore>(ResolveStore<IErasureCodingProfileStore>);
        services.AddSingleton<IMetadataMaintenanceStore>(ResolveStore<IMetadataMaintenanceStore>);
        services.AddSingleton<IStorageMaintenanceOperations>(ResolveStore<IStorageMaintenanceOperations>);
        services.AddSingleton<IObjectPlacementPlanner>(
            _ => new DeterministicObjectPlacementPlanner(configuration["Means:Cluster:PlacementSeed"] ?? "means-v1"));
        services.AddSingleton<IBackgroundTaskRegistry, BackgroundTaskRegistry>();
        services.AddSingleton<IBackgroundTaskManager, BackgroundTaskManager>();
        services.AddSingleton<UploadConcurrencyLimiter>();
        services.AddSingleton<ApiRateLimitStore>();

        services.AddSingleton<BucketPolicyEvaluator>();
        services.AddSingleton<SigV4RequestVerifier>();
        services.AddSingleton<SystemSettingsService>();
        services.AddHostedService<LocalClusterNodeHeartbeatService>();
        services.AddHostedService<DiskHealthIsolationService>();
        services.AddHostedService<ReplicaRepairService>();
        services.AddHostedService<StorageMaintenanceService>();
        services.AddHostedService<MultipartUploadCleanupService>();
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "Means.Console";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.SlidingExpiration = true;
                options.LoginPath = "/";
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();

        return services;
    }

    private static T ResolveStore<T>(IServiceProvider services)
        where T : class
    {
        var backend = services.GetRequiredService<IOptions<XlFsOptions>>().Value.Backend;
        return string.Equals(backend, XlFsOptions.BackendName, StringComparison.OrdinalIgnoreCase)
            ? (T)(object)services.GetRequiredService<XlFsStore>()
            : (T)(object)services.GetRequiredService<SqliteFsStore>();
    }

    private static void AddMeansOpenTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Means:Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!options.Enabled)
        {
            return;
        }

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName) ? "Means" : options.ServiceName.Trim();
        var serviceVersion = string.IsNullOrWhiteSpace(options.ServiceVersion)
            ? typeof(MeansDataPlaneServiceCollectionExtensions).Assembly.GetName().Version?.ToString()
            : options.ServiceVersion.Trim();
        var sampleRatio = Math.Clamp(options.SampleRatio, 0.0, 1.0);
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(
                        serviceName: serviceName,
                        serviceVersion: serviceVersion,
                        serviceInstanceId: Environment.MachineName))
                    .AddSource(MeansTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;
                        instrumentation.EnrichWithHttpRequest = (activity, request) =>
                        {
                            activity.SetTag("means.http.path", request.Path.Value);
                            activity.SetTag("means.http.host", request.Host.Value);
                        };
                    });

                if (sampleRatio < 1.0)
                {
                    tracing.SetSampler(new TraceIdRatioBasedSampler(sampleRatio));
                }

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint.Trim()));
                }
            });
    }

    private static void ValidateConsoleDefaults(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var user = configuration["Means:Console:AdminUser"] ?? ConsoleOptions.DefaultAdminUser;
        var password = configuration["Means:Console:AdminPassword"] ?? ConsoleOptions.DefaultAdminPassword;
        if (user == ConsoleOptions.DefaultAdminUser || password == ConsoleOptions.DefaultAdminPassword)
        {
            throw new InvalidOperationException("Means:Console admin credentials must be changed outside Development.");
        }
    }
}
