namespace Means.Core;

public static class BackgroundTaskStatuses
{
    public const string NeverRun = "NeverRun";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record BackgroundTaskDescriptor(
    string TaskId,
    string Name,
    string Category,
    int IntervalSeconds,
    bool ManualRunSupported = true);

public sealed record BackgroundTaskSnapshot(
    string TaskId,
    string Name,
    string Category,
    int IntervalSeconds,
    bool ManualRunSupported,
    string Status,
    long SuccessCount,
    long FailureCount,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    long? LastDurationMilliseconds,
    string? LastResult,
    string? LastError);

public sealed record BackgroundTaskRunRecord(
    string TaskId,
    string Name,
    string Category,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMilliseconds,
    string? Result,
    string? Error);

public sealed record BackgroundTaskGroup(
    string Category,
    string Name,
    IReadOnlyList<BackgroundTaskSnapshot> Tasks);
