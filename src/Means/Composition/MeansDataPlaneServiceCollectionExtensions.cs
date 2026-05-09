using Means.Core;
using Means.Configuration;
using Means.Infrastructure.SqliteFs;
using Means.Protocol.S3;
using Means.Security;
using Means.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

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
        services.Configure<ConsoleOptions>(configuration.GetSection("Means:Console"));
        services.Configure<RequestLimitsOptions>(configuration.GetSection("Means:RequestLimits"));

        // The SQLite/filesystem adapter implements all three storage-facing ports in v1.
        // Register the concrete singleton once and expose it through focused Core interfaces.
        services.AddSingleton<SqliteFsStore>();
        services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<SqliteFsStore>());
        services.AddSingleton<IAccessKeyStore>(sp => sp.GetRequiredService<SqliteFsStore>());
        services.AddSingleton<IBucketPolicyRepository>(sp => sp.GetRequiredService<SqliteFsStore>());
        services.AddSingleton<IConsoleStore>(sp => sp.GetRequiredService<SqliteFsStore>());

        services.AddSingleton<BucketPolicyEvaluator>();
        services.AddSingleton<SigV4RequestVerifier>();
        services.AddSingleton<SystemSettingsService>();
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
