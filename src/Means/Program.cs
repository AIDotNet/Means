using Means.Composition;
using Means.Endpoints.Cluster;
using Means.Endpoints.Console;
using Means.Endpoints.Metrics;
using Means.Endpoints.S3;
using Means.Middleware;
using Means.Serialization;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, MeansJsonContext.Default);
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddMeansDataPlane(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseMeansApiRateLimits();
app.UseConsoleApiErrors();
app.UseMeansRequestBodyLimits();

app.MapConsoleApi();
app.MapMeansMetrics();
app.MapMeansClusterDataPlane();

// All S3-compatible data-plane requests are resolved by the S3 endpoint router.
// Keeping route mapping behind an extension makes Program.cs stay as the composition root only.
app.MapMeansS3DataPlane(app.Configuration);

app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;
