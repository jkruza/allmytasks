namespace AllMyTasks.Core.Tasks;

/// <summary>Where a task is in its life. Named to avoid clashing with <see cref="System.Threading.Tasks.TaskStatus"/>.</summary>
public enum TaskItemStatus
{
    Open,
    InProgress,
    Completed,
    Cancelled,
}
