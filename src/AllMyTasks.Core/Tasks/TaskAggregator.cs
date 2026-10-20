using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Core.Tasks;

/// <summary>
/// Pulls tasks from every registered <see cref="ITaskSource"/> into one ordered list.
/// </summary>
/// <remarks>
/// Transitional: it syncs from scratch on every call. From M3 views read from the local store instead.
/// </remarks>
/// <param name="sources">The task sources to read.</param>
/// <param name="displayZone">Zone used to order and compare due dates.</param>
public sealed class TaskAggregator(IEnumerable<ITaskSource> sources, TimeZoneInfo displayZone)
{
    private readonly IReadOnlyList<ITaskSource> _sources = [.. sources];

    /// <summary>
    /// Returns open tasks first, then by due date (tasks without one last), then by title.
    /// </summary>
    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var perSource = await Task.WhenAll(_sources.Select(s => GetFromSourceAsync(s, cancellationToken)))
            .ConfigureAwait(false);

        return [.. perSource
            .SelectMany(tasks => tasks)
            .OrderBy(t => !t.IsOpen)
            .ThenBy(t => t.Due is null)
            .ThenBy(t => t.Due?.SortKey(displayZone))
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static async Task<IReadOnlyList<TaskItem>> GetFromSourceAsync(ITaskSource source, CancellationToken cancellationToken)
    {
        var collections = await source.GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new List<TaskItem>();
        foreach (var collection in collections.Where(c => c.Kind == CollectionKind.TaskList))
        {
            var result = await source.SyncTasksAsync(collection, previous: null, cancellationToken).ConfigureAwait(false);
            tasks.AddRange(result.Upserts);
        }

        return tasks;
    }
}
