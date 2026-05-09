namespace Means.Services;

public sealed class ApiRateLimitStore
{
    private readonly object _cleanupSync = new();
    private readonly Dictionary<string, FixedWindowCounter> _counters = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    public bool TryAcquire(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (permitLimit >= int.MaxValue)
        {
            return true;
        }

        permitLimit = Math.Max(1, permitLimit);
        window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : window;
        CleanupExpiredCounters(now);

        lock (_cleanupSync)
        {
            if (!_counters.TryGetValue(partitionKey, out var counter) || now >= counter.WindowStartedAt + window)
            {
                _counters[partitionKey] = new FixedWindowCounter(now, Count: 1);
                return true;
            }

            if (counter.Count < permitLimit)
            {
                _counters[partitionKey] = counter with { Count = counter.Count + 1 };
                return true;
            }

            retryAfter = counter.WindowStartedAt + window - now;
            return false;
        }
    }

    private void CleanupExpiredCounters(DateTimeOffset now)
    {
        if (now - _lastCleanup < TimeSpan.FromMinutes(1))
        {
            return;
        }

        lock (_cleanupSync)
        {
            if (now - _lastCleanup < TimeSpan.FromMinutes(1))
            {
                return;
            }

            var cutoff = now.Subtract(TimeSpan.FromDays(1));
            foreach (var key in _counters.Where(pair => pair.Value.WindowStartedAt < cutoff).Select(pair => pair.Key).ToArray())
            {
                _counters.Remove(key);
            }

            _lastCleanup = now;
        }
    }

    private sealed record FixedWindowCounter(DateTimeOffset WindowStartedAt, int Count);
}
