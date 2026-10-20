using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Tasks;

/// <summary>
/// A task as shown in All My Tasks, regardless of which provider it came from.
/// </summary>
/// <param name="Key">Identity: source, collection (task list) and provider id.</param>
/// <param name="Title">Short, human-readable title. Never null; an empty title is replaced in the view.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Due">When the task is due, if it has a due date.</param>
/// <param name="Start">"Do not show before"; optional.</param>
/// <param name="Status">Open, in progress, completed or cancelled.</param>
/// <param name="CompletedAt">When it was completed, if the provider says.</param>
/// <param name="Priority">Normalised priority.</param>
/// <param name="Labels">Provider labels or categories; may be empty.</param>
/// <param name="ParentKey">The parent task, for subtasks.</param>
/// <param name="Recurrence">Null for non-recurring tasks.</param>
/// <param name="Url">Link to the task in the provider.</param>
/// <param name="LastModified">When the provider last changed it.</param>
public sealed record TaskItem(
    ItemKey Key,
    string Title,
    string? Notes = null,
    TaskDue? Due = null,
    DateTimeOffset? Start = null,
    TaskItemStatus Status = TaskItemStatus.Open,
    DateTimeOffset? CompletedAt = null,
    Priority Priority = Priority.None,
    IReadOnlyList<string>? Labels = null,
    ItemKey? ParentKey = null,
    Recurrence? Recurrence = null,
    Uri? Url = null,
    DateTimeOffset? LastModified = null)
{
    public ItemKey Key { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Key);

    public string Title { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Title);

    public IReadOnlyList<string> Labels { get; init => field = value ?? []; } = Labels ?? [];

    /// <summary>True when the task is done.</summary>
    public bool IsCompleted => Status == TaskItemStatus.Completed;

    /// <summary>True when the task still needs doing (open or in progress).</summary>
    public bool IsOpen => Status is TaskItemStatus.Open or TaskItemStatus.InProgress;

    /// <summary>True when the task is open and its due value has passed at <paramref name="now"/>.</summary>
    public bool IsOverdue(DateTimeOffset now, TimeZoneInfo displayZone) =>
        IsOpen && Due is { } due && due.IsPast(now, displayZone);
}
