using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>Everything the details pane shows about one task, resolved from the cache.</summary>
/// <param name="Task">The task itself.</param>
/// <param name="CollectionName">Name of the task list it belongs to; empty if the list is unknown.</param>
/// <param name="CollectionColor">Colour of the list (<c>#RRGGBB</c>), if any.</param>
/// <param name="SourceName">Name of the source it came from; empty if the source is unknown.</param>
/// <param name="Parent">The parent task, when <see cref="TaskItem.ParentKey"/> is set and the parent is cached.</param>
/// <param name="Subtasks">Direct subtasks: open ones first, then by due date, then by title.</param>
/// <param name="IsOverdue">Open and past due at the time of the query.</param>
public sealed record TaskDetails(
    TaskItem Task,
    string CollectionName,
    string? CollectionColor,
    string SourceName,
    TaskItem? Parent,
    IReadOnlyList<TaskItem> Subtasks,
    bool IsOverdue);

public interface ITaskDetailsService
{
    /// <summary>Returns the details of the task with <paramref name="key"/>, or null if it is no longer cached.</summary>
    Task<TaskDetails?> GetAsync(ItemKey key, TimeZoneInfo displayZone, CancellationToken cancellationToken = default);
}

/// <summary>Builds <see cref="TaskDetails"/> from the store.</summary>
public sealed class TaskDetailsService(IItemStore store, ISourceRepository sources, TimeProvider clock) : ITaskDetailsService
{
    public async Task<TaskDetails?> GetAsync(ItemKey key, TimeZoneInfo displayZone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(displayZone);
        var tasks = await store.GetTasksAsync(cancellationToken).ConfigureAwait(false);
        var task = tasks.FirstOrDefault(t => t.Key == key);
        if (task is null)
        {
            return null;
        }

        var collection = (await sources.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.SourceId == key.SourceId && c.Id == key.CollectionId);
        var source = (await sources.GetSourcesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == key.SourceId);
        cancellationToken.ThrowIfCancellationRequested();

        // A task that names itself as its parent has no parent; longer cycles only matter to the list's nesting.
        var parent = task.ParentKey is { } parentKey && parentKey != key
            ? tasks.FirstOrDefault(t => t.Key == parentKey)
            : null;
        var subtasks = tasks
            .Where(t => t.ParentKey == key && t.Key != key && t.Key != parent?.Key)
            .OrderBy(t => t.IsOpen ? 0 : 1)
            .ThenBy(t => t.Due is null ? 1 : 0)
            .ThenBy(t => t.Due?.SortKey(displayZone) ?? DateTimeOffset.MaxValue)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new TaskDetails(
            task,
            collection?.DisplayName ?? string.Empty,
            collection?.Color,
            source?.DisplayName ?? string.Empty,
            parent,
            subtasks,
            task.IsOverdue(clock.GetUtcNow(), displayZone));
    }
}
