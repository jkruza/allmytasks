using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class TaskAggregatorTests
{
    private static readonly DateOnly Day = new(2026, 10, 19);

    private static TaskItem Task(string source, string id, string title, TaskDue? due = null, TaskItemStatus status = TaskItemStatus.Open) =>
        new(TestData.Key(id, source), title, Due: due, Status: status);

    private static InMemoryTaskSource Source(string id, params TaskItem[] tasks) =>
        new(id, id.ToUpperInvariant(), [TestData.TaskList(id)], tasks);

    [Fact]
    public async Task GetAllAsync_merges_sources_and_orders_open_tasks_by_due_date()
    {
        var a = Source("s1",
            Task("s1", "1", "No date"),
            Task("s1", "2", "Done", new DueDate(Day), TaskItemStatus.Completed),
            Task("s1", "3", "Tomorrow", new DueDate(Day.AddDays(1))));
        var b = Source("s2",
            Task("s2", "1", "Today", new DueDate(Day)));

        var tasks = await new TaskAggregator([a, b], TimeZoneInfo.Utc).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Today", "Tomorrow", "No date", "Done"], tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task GetAllAsync_puts_date_only_before_timed_on_the_same_day()
    {
        var source = Source("s1",
            Task("s1", "1", "Timed", new DueDateTime(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero))),
            Task("s1", "2", "Date only", new DueDate(Day)));

        var tasks = await new TaskAggregator([source], TimeZoneInfo.Utc).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Date only", "Timed"], tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task GetAllAsync_sorts_cancelled_tasks_after_open_ones()
    {
        var source = Source("s1",
            Task("s1", "1", "Cancelled", new DueDate(Day), TaskItemStatus.Cancelled),
            Task("s1", "2", "Open"));

        var tasks = await new TaskAggregator([source], TimeZoneInfo.Utc).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Open", "Cancelled"], tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task GetAllAsync_reads_every_task_list_of_a_source_and_skips_calendars()
    {
        var source = new InMemoryTaskSource("s1", "S1",
            [TestData.TaskList("s1", "work"), TestData.TaskList("s1", "home"), TestData.Calendar("s1", "cal")],
            [
                new TaskItem(TestData.Key("1", "s1", "work"), "Work"),
                new TaskItem(TestData.Key("2", "s1", "home"), "Home"),
                new TaskItem(TestData.Key("3", "s1", "cal"), "Not a task list"),
            ]);

        var tasks = await new TaskAggregator([source], TimeZoneInfo.Utc).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Home", "Work"], tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task GetAllAsync_returns_empty_list_without_sources()
    {
        var tasks = await new TaskAggregator([], TimeZoneInfo.Utc).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(tasks);
    }
}
