using Means.Infrastructure.XlFs;

namespace Means.Services;

/// <summary>
/// Forces the local metadata store to initialize during host startup.
/// This surfaces WAL/path ownership problems before background maintenance workers begin running.
/// </summary>
public sealed class MetadataStoreWarmupService : IHostedService
{
    private readonly XlFsStore _store;

    public MetadataStoreWarmupService(XlFsStore store)
    {
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _store.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
