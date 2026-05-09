namespace Means.Core;

public interface IBackgroundTaskManager
{
    IReadOnlyList<BackgroundTaskSnapshot> ListTasks();

    IReadOnlyList<BackgroundTaskGroup> ListGroups();

    IReadOnlyList<BackgroundTaskRunRecord> ListHistory(string? taskId, int limit);

    Task<BackgroundTaskSnapshot> RunTaskAsync(string taskId, CancellationToken cancellationToken);
}
