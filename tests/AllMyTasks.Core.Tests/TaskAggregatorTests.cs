using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class TaskAggregatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAllAsync_merges_sources_and_orders_open_tasks_by_due_date()
    {
        var a = new InMemoryTaskSource("a", "A",
        [
            new TaskItem("1", "a", "No date"),
            new TaskItem("2", "a", "Done", Day, IsCompleted: true),
            new TaskItem("3", "a", "Tomorrow", Day.AddDays(1)),
        ]);
        var b = new InMemoryTaskSource("b", "B",
        [
            new TaskItem("1", "b", "Today", Day),
        ]);

        var tasks = await new TaskAggregator([a, b]).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Today", "Tomorrow", "No date", "Done"], tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task GetAllAsync_returns_empty_list_without_sources()
    {
        var tasks = await new TaskAggregator([]).GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(tasks);
    }
}
