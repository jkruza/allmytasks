using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>Builds the All tasks list from the store.</summary>
public sealed class TaskListService(IItemStore store, ISourceRepository sources, TimeProvider clock) : ITaskListService
{
    public async Task<IReadOnlyList<TaskGroup>> GetAsync(TaskListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tasks = await store.GetTasksAsync(cancellationToken).ConfigureAwait(false);
        var collections = (await sources.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => (c.SourceId, c.Id));
        var sourceNames = (await sources.GetSourcesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.Id, s => s.DisplayName);
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.GetUtcNow();
        var zone = query.DisplayZone;

        var visible = tasks.Where(t =>
            !collections.TryGetValue((t.Key.SourceId, t.Key.CollectionId), out var c) || c.IsVisible);
        var filtered = visible.Where(t => query.Filter.Matches(t, now, zone)).ToList();
        var sorted = Sort(filtered, query.Sort, zone, sourceNames);

        var keys = sorted.Select(t => t.Key).ToHashSet();
        var childrenOf = sorted.Where(t => t.ParentKey is { } p && keys.Contains(p) && p != t.Key)
            .ToLookup(t => t.ParentKey!);

        var visited = new HashSet<ItemKey>();

        TaskRow ToRow(TaskItem task)
        {
            collections.TryGetValue((task.Key.SourceId, task.Key.CollectionId), out var collection);
            sourceNames.TryGetValue(task.Key.SourceId, out var sourceName);
            visited.Add(task.Key);
            var children = childrenOf[task.Key].Where(c => !visited.Contains(c.Key)).Select(ToRow).ToList();
            return new TaskRow(task, collection?.DisplayName ?? string.Empty, sourceName ?? string.Empty, collection?.Color, task.IsOverdue(now, zone), children);
        }

        // A subtask whose parent was filtered out is shown at the top level so it never disappears.
        var top = sorted.Where(t => t.ParentKey is not { } p || !keys.Contains(p) || p == t.Key)
            .Select(ToRow)
            .ToList();

        // Tasks only reachable through a parent cycle have no root; show them at the top level too.
        foreach (var task in sorted.Where(t => !visited.Contains(t.Key)).ToList())
        {
            if (!visited.Contains(task.Key))
            {
                top.Add(ToRow(task));
            }
        }

        return Group(top, query.Grouping, now, zone);
    }

    private static List<TaskItem> Sort(List<TaskItem> tasks, TaskSort sort, TimeZoneInfo zone, Dictionary<string, string> sourceNames)
    {
        string SourceOf(TaskItem t) => sourceNames.GetValueOrDefault(t.Key.SourceId, t.Key.SourceId);

        // Values that are missing sort last whichever way the list is ordered.
        return sort.Field switch
        {
            TaskSortField.Due => OrderNullsLast(tasks, t => t.Due?.SortKey(zone), sort.Descending),
            TaskSortField.LastModified => OrderNullsLast(tasks, t => t.LastModified, sort.Descending),
            TaskSortField.Priority => Order(tasks, t => (int)t.Priority, sort.Descending),
            TaskSortField.Title => Order(tasks, t => t.Title, sort.Descending, StringComparer.CurrentCultureIgnoreCase),
            TaskSortField.Source => Order(tasks, SourceOf, sort.Descending, StringComparer.CurrentCultureIgnoreCase),
            _ => tasks,
        };
    }

    private static List<TaskItem> Order<TKey>(List<TaskItem> tasks, Func<TaskItem, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        [.. descending ? tasks.OrderByDescending(key, comparer) : tasks.OrderBy(key, comparer)];

    private static List<TaskItem> OrderNullsLast<TKey>(List<TaskItem> tasks, Func<TaskItem, TKey?> key, bool descending)
        where TKey : struct, IComparable<TKey>
    {
        var withValue = tasks.Where(t => key(t) is not null);
        var without = tasks.Where(t => key(t) is null);
        var ordered = descending ? withValue.OrderByDescending(t => key(t)) : withValue.OrderBy(t => key(t));
        return [.. ordered, .. without];
    }

    private static List<TaskGroup> Group(List<TaskRow> rows, TaskGrouping grouping, DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (grouping)
        {
            case TaskGrouping.DueBucket:
                return [.. Enum.GetValues<DueBucket>()
                    .Select(b => new TaskGroup(string.Empty, b, null, [.. rows.Where(r => BucketOf(r, now, zone) == b)]))
                    .Where(g => g.Rows.Count > 0)];
            case TaskGrouping.Collection:
                return [.. rows.GroupBy(r => r.CollectionName)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new TaskGroup(g.Key, null, null, [.. g]))];
            case TaskGrouping.Priority:
                return [.. rows.GroupBy(r => r.Task.Priority)
                    .OrderByDescending(g => g.Key)
                    .Select(g => new TaskGroup(g.Key.ToString(), null, g.Key, [.. g]))];
            case TaskGrouping.Label:
                var labels = rows.SelectMany(r => r.Task.Labels).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.CurrentCultureIgnoreCase);
                var groups = labels.Select(l => new TaskGroup(l, null, null,
                    [.. rows.Where(r => r.Task.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))])).ToList();
                var unlabelled = rows.Where(r => r.Task.Labels.Count == 0).ToList();
                if (unlabelled.Count > 0)
                {
                    groups.Add(new TaskGroup(string.Empty, null, null, unlabelled));
                }

                return groups;
            default:
                return rows.Count == 0 ? [] : [new TaskGroup(string.Empty, null, null, rows)];
        }
    }

    internal static DueBucket BucketOf(TaskRow row, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (row.Task.Due is null)
        {
            return DueBucket.NoDate;
        }

        if (row.IsOverdue)
        {
            return DueBucket.Overdue;
        }

        var today = now.Today(zone);
        var day = DueFilter.DueDay(row.Task, zone)!.Value;
        if (day < today)
        {
            return DueBucket.Earlier;
        }

        return day == today ? DueBucket.Today : day < today.AddDays(7) ? DueBucket.ThisWeek : DueBucket.Later;
    }
}
