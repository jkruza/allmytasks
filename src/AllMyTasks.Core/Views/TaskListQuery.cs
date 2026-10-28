using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

public enum TaskSortField
{
    Due,
    Priority,
    Title,
    Source,
    LastModified,
}

public enum TaskGrouping
{
    None,
    DueBucket,
    Collection,
    Priority,
    Label,
}

/// <summary>Due-date groups. <c>Earlier</c> holds closed tasks whose due day is past, which are not overdue.</summary>
public enum DueBucket
{
    Overdue,
    Earlier,
    Today,
    ThisWeek,
    Later,
    NoDate,
}

/// <summary>How to order rows. Tasks without the sorted value always go last, in either direction.</summary>
public sealed record TaskSort(TaskSortField Field = TaskSortField.Due, bool Descending = false);

/// <summary>Everything the All tasks list depends on.</summary>
public sealed record TaskListQuery(TaskFilter Filter, TaskSort Sort, TaskGrouping Grouping, TimeZoneInfo DisplayZone);

/// <summary>A task prepared for the list, with its subtasks nested underneath.</summary>
/// <param name="Task">The task.</param>
/// <param name="CollectionName">Name of its collection.</param>
/// <param name="SourceName">Name of its source.</param>
/// <param name="Color">Collection colour.</param>
/// <param name="IsOverdue">Whether to highlight the due value.</param>
/// <param name="Children">Subtasks that passed the filter.</param>
/// <param name="CanChange">Whether the task can be completed and reopened from the app.</param>
/// <param name="Pending">The change that has not reached the source yet, if any.</param>
public sealed record TaskRow(
    TaskItem Task,
    string CollectionName,
    string SourceName,
    string? Color,
    bool IsOverdue,
    IReadOnlyList<TaskRow> Children,
    bool CanChange = false,
    PendingChange? Pending = null);

/// <summary>A heading and its rows. For <see cref="TaskGrouping.None"/> there is one group with an empty label.</summary>
/// <param name="Label">Collection name, label or priority name; empty for the other groupings, where the view localises <paramref name="Bucket"/>.</param>
/// <param name="Bucket">Set for <see cref="TaskGrouping.DueBucket"/>.</param>
/// <param name="Priority">Set for <see cref="TaskGrouping.Priority"/>.</param>
/// <param name="Rows">Top-level rows with nested subtasks.</param>
public sealed record TaskGroup(string Label, DueBucket? Bucket, Priority? Priority, IReadOnlyList<TaskRow> Rows);

public interface ITaskListService
{
    /// <summary>Filters, sorts, groups and nests the cached tasks. Supports cancellation for per-keystroke use.</summary>
    Task<IReadOnlyList<TaskGroup>> GetAsync(TaskListQuery query, CancellationToken cancellationToken = default);
}
