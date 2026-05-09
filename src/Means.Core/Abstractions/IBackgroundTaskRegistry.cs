namespace Means.Core;

public interface IBackgroundTaskRegistry
{
    void Register(BackgroundTaskDescriptor descriptor);

    Task RunAsync(
        BackgroundTaskDescriptor descriptor,
        Func<CancellationToken, Task<string?>> operation,
        CancellationToken cancellationToken);

    BackgroundTaskSnapshot? GetTask(string taskId);

    IReadOnlyList<BackgroundTaskSnapshot> ListTasks();

    IReadOnlyList<BackgroundTaskRunRecord> ListHistory(string? taskId, int limit);
}
