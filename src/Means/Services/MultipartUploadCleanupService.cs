using Means.Infrastructure.SqliteFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Periodically removes abandoned multipart uploads and their part files.
/// Frontend abort is best-effort, so server-side cleanup is required to avoid leaked storage.
/// </summary>
public sealed class MultipartUploadCleanupService(
    SqliteFsStore store,
    IOptions<SqliteFsOptions> options,
    ILogger<MultipartUploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupOnceAsync(stoppingToken);

            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.MultipartUploadCleanupIntervalMinutes));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        var age = TimeSpan.FromHours(Math.Max(1, options.Value.MultipartUploadCleanupAgeHours));
        var cutoff = DateTimeOffset.UtcNow.Subtract(age);
        try
        {
            var cleaned = await store.CleanupMultipartUploadsAsync(cutoff, cancellationToken);
            if (cleaned > 0)
            {
                logger.LogInformation("Cleaned {UploadCount} abandoned multipart uploads older than {CutoffUtc}.", cleaned, cutoff);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean abandoned multipart uploads.");
        }
    }
}
