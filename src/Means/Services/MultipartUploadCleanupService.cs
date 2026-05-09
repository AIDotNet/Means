using Means.Core;
using Means.Infrastructure.SqliteFs;
using Microsoft.Extensions.Options;

namespace Means.Services;

/// <summary>
/// Periodically removes abandoned multipart uploads and their part files.
/// Frontend abort is best-effort, so server-side cleanup is required to avoid leaked storage.
/// </summary>
public sealed class MultipartUploadCleanupService : BackgroundService
{
    private readonly IStorageMaintenanceOperations _store;
    private readonly IOptions<SqliteFsOptions> _options;
    private readonly IBackgroundTaskRegistry _backgroundTasks;
    private readonly ILogger<MultipartUploadCleanupService> _logger;
    private readonly BackgroundTaskDescriptor _task;

    public MultipartUploadCleanupService(
        IStorageMaintenanceOperations store,
        IOptions<SqliteFsOptions> options,
        IBackgroundTaskRegistry backgroundTasks,
        ILogger<MultipartUploadCleanupService> logger)
    {
        _store = store;
        _options = options;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _task = BackgroundTaskDescriptors.MultipartCleanup(_options.Value.MultipartUploadCleanupIntervalMinutes);
        _backgroundTasks.Register(_task);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupOnceAsync(stoppingToken);

            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.Value.MultipartUploadCleanupIntervalMinutes));
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
        var age = TimeSpan.FromHours(Math.Max(1, _options.Value.MultipartUploadCleanupAgeHours));
        var cutoff = DateTimeOffset.UtcNow.Subtract(age);
        try
        {
            await _backgroundTasks.RunAsync(
                _task,
                async token =>
                {
                    var cleaned = await _store.CleanupMultipartUploadsAsync(cutoff, token);
                    if (cleaned > 0)
                    {
                        _logger.LogInformation(
                            "Cleaned {UploadCount} abandoned multipart uploads older than {CutoffUtc}.",
                            cleaned,
                            cutoff);
                    }

                    return $"cleaned={cleaned}; cutoff={cutoff:O}";
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean abandoned multipart uploads.");
        }
    }
}
