using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// A task source backed by a fixed list. Used for samples and tests until real providers exist.
/// </summary>
public sealed class InMemoryTaskSource(string id, string displayName, IEnumerable<TaskItem> tasks) : ITaskSource
{
    private readonly IReadOnlyList<TaskItem> _tasks = [.. tasks];

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks);
}
