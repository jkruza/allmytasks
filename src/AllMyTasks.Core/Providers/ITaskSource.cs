using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// A provider of tasks, such as a local list, Todoist or Microsoft To Do.
/// </summary>
public interface ITaskSource
{
    /// <summary>Stable identifier for this source, used in <see cref="TaskItem.SourceId"/>.</summary>
    string Id { get; }

    /// <summary>Name shown to the user.</summary>
    string DisplayName { get; }

    Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default);
}
