using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Providers;

/// <summary>A source that serves tasks, such as a local list, Todoist or Microsoft To Do.</summary>
public interface ITaskSource : ISource
{
    /// <summary>
    /// Returns what changed in <paramref name="collection"/> since <paramref name="previous"/>.
    /// A null <paramref name="previous"/> means first sync and yields a full snapshot.
    /// Throws <see cref="SyncStateExpiredException"/> if <paramref name="previous"/> is no longer valid.
    /// </summary>
    Task<SyncResult<TaskItem>> SyncTasksAsync(
        CollectionInfo collection,
        SyncState? previous,
        CancellationToken cancellationToken = default);
}
