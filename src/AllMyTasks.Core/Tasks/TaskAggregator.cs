using AllMyTasks.Core.Providers;

namespace AllMyTasks.Core.Tasks;

/// <summary>
/// Pulls tasks from every registered <see cref="ITaskSource"/> into one ordered list.
/// </summary>
public sealed class TaskAggregator(IEnumerable<ITaskSource> sources)
{
    private readonly IReadOnlyList<ITaskSource> _sources = [.. sources];

    /// <summary>
    /// Returns open tasks first, then by due date (tasks without one last), then by title.
    /// </summary>
    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var perSource = await Task.WhenAll(_sources.Select(s => s.GetTasksAsync(cancellationToken)))
            .ConfigureAwait(false);

        return [.. perSource
            .SelectMany(tasks => tasks)
            .OrderBy(t => t.IsCompleted)
            .ThenBy(t => t.Due is null)
            .ThenBy(t => t.Due)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)];
    }
}
