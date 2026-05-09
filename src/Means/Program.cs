using Means.Composition;
using Means.Endpoints.Console;
using Means.Endpoints.Metrics;
using Means.Endpoints.S3;
using Means.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddMeansDataPlane(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseMeansApiRateLimits();
app.UseConsoleApiErrors();
app.UseMeansRequestBodyLimits();

app.MapConsoleApi();
app.MapMeansMetrics();

// All S3-compatible data-plane requests are resolved by the S3 endpoint router.
// Keeping route mapping behind an extension makes Program.cs stay as the composition root only.
app.MapMeansS3DataPlane(app.Configuration);

app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;
