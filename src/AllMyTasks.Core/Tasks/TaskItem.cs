namespace AllMyTasks.Core.Tasks;

/// <summary>
/// A task as shown in All My Tasks, regardless of which provider it came from.
/// </summary>
/// <param name="Id">Identifier, unique within its <paramref name="SourceId"/>.</param>
/// <param name="SourceId">The <see cref="Providers.ITaskSource.Id"/> the task came from.</param>
/// <param name="Title">Short, human-readable title.</param>
/// <param name="Due">When the task is due, if it has a due date.</param>
/// <param name="IsCompleted">Whether the task is done.</param>
public sealed record TaskItem(
    string Id,
    string SourceId,
    string Title,
    DateTimeOffset? Due = null,
    bool IsCompleted = false)
{
    /// <summary>True when the task is open and its due date is before <paramref name="now"/>.</summary>
    public bool IsOverdue(DateTimeOffset now) => !IsCompleted && Due is { } due && due < now;
}
