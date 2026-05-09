using System.Diagnostics;
using Means.Core;

namespace Means.Services;

public sealed class BackgroundTaskRegistry : IBackgroundTaskRegistry
{
    private const int MaxHistoryItems = 200;

    private readonly object _sync = new();
    private readonly Dictionary<string, BackgroundTaskState> _tasks = new(StringComparer.Ordinal);
    private readonly List<BackgroundTaskRunRecord> _history = [];

    public void Register(BackgroundTaskDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        lock (_sync)
        {
            if (_tasks.TryGetValue(descriptor.TaskId, out var existing))
            {
                existing.Descriptor = descriptor;
                return;
            }

            _tasks[descriptor.TaskId] = new BackgroundTaskState(descriptor);
        }
    }

    public async Task RunAsync(
        BackgroundTaskDescriptor descriptor,
        Func<CancellationToken, Task<string?>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Register(descriptor);

        BackgroundTaskState state;
        lock (_sync)
        {
            state = _tasks[descriptor.TaskId];
        }

        await state.RunLock.WaitAsync(cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        using var activity = MeansTelemetry.ActivitySource.StartActivity("means.background_task");
        activity?.SetTag("means.task.id", descriptor.TaskId);
        activity?.SetTag("means.task.name", descriptor.Name);
        activity?.SetTag("means.task.category", descriptor.Category);
        activity?.SetTag("means.task.interval_seconds", descriptor.IntervalSeconds);
        try
        {
            lock (_sync)
            {
                state.Status = BackgroundTaskStatuses.Running;
                state.LastStartedAt = startedAt;
                state.LastCompletedAt = null;
                state.LastDurationMilliseconds = null;
                state.LastError = null;
            }

            var result = await operation(cancellationToken);
            Complete(descriptor.TaskId, startedAt, BackgroundTaskStatuses.Succeeded, result, null);
            activity?.SetTag("means.task.status", BackgroundTaskStatuses.Succeeded);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(descriptor.TaskId, startedAt, BackgroundTaskStatuses.Cancelled, null, null);
            activity?.SetTag("means.task.status", BackgroundTaskStatuses.Cancelled);
            activity?.SetStatus(ActivityStatusCode.Error, "Background task cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Complete(descriptor.TaskId, startedAt, BackgroundTaskStatuses.Failed, null, ex.Message);
            activity?.SetTag("means.task.status", BackgroundTaskStatuses.Failed);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            state.RunLock.Release();
        }
    }

    public BackgroundTaskSnapshot? GetTask(string taskId)
    {
        lock (_sync)
        {
            return _tasks.TryGetValue(taskId, out var state)
                ? state.ToSnapshot()
                : null;
        }
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks()
    {
        lock (_sync)
        {
            return _tasks.Values
                .Select(state => state.ToSnapshot())
                .OrderBy(task => task.Category, StringComparer.Ordinal)
                .ThenBy(task => task.TaskId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<BackgroundTaskRunRecord> ListHistory(string? taskId, int limit)
    {
        var normalizedTaskId = string.IsNullOrWhiteSpace(taskId) ? null : taskId.Trim();
        var boundedLimit = Math.Clamp(limit, 1, MaxHistoryItems);
        lock (_sync)
        {
            return _history
                .Where(item => normalizedTaskId is null || string.Equals(item.TaskId, normalizedTaskId, StringComparison.Ordinal))
                .Take(boundedLimit)
                .ToArray();
        }
    }

    private void Complete(string taskId, DateTimeOffset startedAt, string status, string? result, string? error)
    {
        var completedAt = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            var state = _tasks[taskId];
            var duration = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds);
            var trimmedResult = Trim(result, 512);
            var trimmedError = Trim(error, 1024);
            state.Status = status;
            state.LastCompletedAt = completedAt;
            state.LastDurationMilliseconds = duration;
            state.LastResult = trimmedResult;
            state.LastError = trimmedError;
            if (status == BackgroundTaskStatuses.Succeeded)
            {
                state.SuccessCount++;
            }
            else if (status == BackgroundTaskStatuses.Failed)
            {
                state.FailureCount++;
            }

            _history.Insert(0, new BackgroundTaskRunRecord(
                state.Descriptor.TaskId,
                state.Descriptor.Name,
                state.Descriptor.Category,
                status,
                startedAt,
                completedAt,
                duration,
                trimmedResult,
                trimmedError));
            if (_history.Count > MaxHistoryItems)
            {
                _history.RemoveRange(MaxHistoryItems, _history.Count - MaxHistoryItems);
            }
        }
    }

    private static void ValidateDescriptor(BackgroundTaskDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.TaskId)
            || string.IsNullOrWhiteSpace(descriptor.Name)
            || string.IsNullOrWhiteSpace(descriptor.Category)
            || descriptor.IntervalSeconds <= 0)
        {
            throw new ArgumentException("Background task descriptor fields are required.", nameof(descriptor));
        }
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class BackgroundTaskState(BackgroundTaskDescriptor descriptor)
    {
        public BackgroundTaskDescriptor Descriptor { get; set; } = descriptor;

        public SemaphoreSlim RunLock { get; } = new(1, 1);

        public string Status { get; set; } = BackgroundTaskStatuses.NeverRun;

        public long SuccessCount { get; set; }

        public long FailureCount { get; set; }

        public DateTimeOffset? LastStartedAt { get; set; }

        public DateTimeOffset? LastCompletedAt { get; set; }

        public long? LastDurationMilliseconds { get; set; }

        public string? LastResult { get; set; }

        public string? LastError { get; set; }

        public BackgroundTaskSnapshot ToSnapshot()
        {
            return new BackgroundTaskSnapshot(
                Descriptor.TaskId,
                Descriptor.Name,
                Descriptor.Category,
                Descriptor.IntervalSeconds,
                Descriptor.ManualRunSupported,
                Status,
                SuccessCount,
                FailureCount,
                LastStartedAt,
                LastCompletedAt,
                LastDurationMilliseconds,
                LastResult,
                LastError);
        }
    }
}
