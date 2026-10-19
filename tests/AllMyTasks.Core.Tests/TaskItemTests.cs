using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class TaskItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsOverdue_is_true_for_open_task_due_in_the_past() =>
        Assert.True(new TaskItem("1", "s", "t", Now.AddHours(-1)).IsOverdue(Now));

    [Fact]
    public void IsOverdue_is_false_for_completed_task() =>
        Assert.False(new TaskItem("1", "s", "t", Now.AddHours(-1), IsCompleted: true).IsOverdue(Now));

    [Fact]
    public void IsOverdue_is_false_without_due_date() =>
        Assert.False(new TaskItem("1", "s", "t").IsOverdue(Now));
}
