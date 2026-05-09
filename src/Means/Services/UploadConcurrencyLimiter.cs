using Means.Configuration;
using Microsoft.Extensions.Options;

namespace Means.Services;

public sealed class UploadConcurrencyLimiter
{
    private readonly SemaphoreSlim _semaphore;

    public UploadConcurrencyLimiter(IOptions<RequestLimitsOptions> options)
    {
        MaxConcurrentUploads = Math.Clamp(options.Value.MaxConcurrentUploadRequests, 1, 100000);
        _semaphore = new SemaphoreSlim(MaxConcurrentUploads, MaxConcurrentUploads);
    }

    public int MaxConcurrentUploads { get; }

    public int InFlightUploads => MaxConcurrentUploads - _semaphore.CurrentCount;

    public ValueTask<UploadConcurrencyLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _semaphore.Wait(0)
            ? ValueTask.FromResult<UploadConcurrencyLease?>(new UploadConcurrencyLease(_semaphore))
            : ValueTask.FromResult<UploadConcurrencyLease?>(null);
    }
}

public sealed class UploadConcurrencyLease : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    internal UploadConcurrencyLease(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
