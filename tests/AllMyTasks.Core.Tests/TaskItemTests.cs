using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class TaskItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TaskItem Task(TaskDue? due = null, TaskItemStatus status = TaskItemStatus.Open) =>
        new(TestData.Key(), "t", Due: due, Status: status);

    [Fact]
    public void IsOverdue_is_true_for_open_task_due_in_the_past() =>
        Assert.True(Task(new DueDateTime(Now.AddHours(-1))).IsOverdue(Now, Utc));

    [Fact]
    public void IsOverdue_is_true_for_in_progress_task_due_in_the_past() =>
        Assert.True(Task(new DueDateTime(Now.AddHours(-1)), TaskItemStatus.InProgress).IsOverdue(Now, Utc));

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    public void IsOverdue_is_false_for_closed_task(TaskItemStatus status) =>
        Assert.False(Task(new DueDateTime(Now.AddHours(-1)), status).IsOverdue(Now, Utc));

    [Fact]
    public void IsOverdue_is_false_without_due_date() =>
        Assert.False(Task().IsOverdue(Now, Utc));

    [Fact]
    public void IsOverdue_is_false_for_date_only_task_due_today() =>
        Assert.False(Task(new DueDate(new DateOnly(2026, 10, 19))).IsOverdue(Now, Utc));

    [Fact]
    public void IsCompleted_is_derived_from_status()
    {
        Assert.True(Task(status: TaskItemStatus.Completed).IsCompleted);
        Assert.False(Task(status: TaskItemStatus.Cancelled).IsCompleted);
        Assert.False(Task().IsCompleted);
    }

    [Fact]
    public void Defaults_are_open_with_no_priority_and_no_labels()
    {
        var task = Task();

        Assert.Equal(TaskItemStatus.Open, task.Status);
        Assert.Equal(Priority.None, task.Priority);
        Assert.Empty(task.Labels);
    }

    [Fact]
    public void Title_must_not_be_null() =>
        Assert.Throws<ArgumentNullException>(() => new TaskItem(TestData.Key(), null!));

    [Fact]
    public void Subtask_references_its_parent()
    {
        var parent = TestData.Key("parent");
        var child = new TaskItem(TestData.Key("child"), "sub", ParentKey: parent);

        Assert.Equal(parent, child.ParentKey);
    }
}
