using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class InMemorySourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static InMemoryTaskSource TaskSource() => new("s", "S", [TestData.TaskList(), TestData.TaskList("s", "other")],
    [
        new TaskItem(TestData.Key("1"), "In c"),
        new TaskItem(TestData.Key("2", collection: "other"), "In other"),
    ]);

    private static InMemoryCalendarSource CalendarSource() => new("s", "S", [TestData.Calendar()],
    [
        new CalendarEvent(TestData.Key("1"), "Meeting", new TimedEvent(T0, T0.AddHours(1))),
        new CalendarEvent(TestData.Key("2", collection: "other"), "Elsewhere", new TimedEvent(T0, T0.AddHours(1))),
    ]);

    [Fact]
    public async Task First_task_sync_is_a_snapshot_of_that_collection_only()
    {
        var result = await TaskSource().SyncTasksAsync(TestData.TaskList(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["In c"], result.Upserts.Select(t => t.Title));
        Assert.Empty(result.DeletedItemIds);
    }

    [Fact]
    public async Task Task_sync_with_returned_state_is_an_empty_delta()
    {
        var source = TaskSource();
        var first = await source.SyncTasksAsync(TestData.TaskList(), null, TestContext.Current.CancellationToken);

        var second = await source.SyncTasksAsync(TestData.TaskList(), first.NewState, TestContext.Current.CancellationToken);

        Assert.False(second.IsFullSnapshot);
        Assert.Empty(second.Upserts);
    }

    [Fact]
    public async Task Task_sync_with_unknown_state_reports_expiry() =>
        await Assert.ThrowsAsync<SyncStateExpiredException>(() =>
            TaskSource().SyncTasksAsync(TestData.TaskList(), new SyncState("bogus"), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Calendar_sync_follows_the_same_contract()
    {
        var source = CalendarSource();
        var collection = TestData.Calendar();

        var first = await source.SyncEventsAsync(collection, null, TestContext.Current.CancellationToken);
        var second = await source.SyncEventsAsync(collection, first.NewState, TestContext.Current.CancellationToken);

        Assert.True(first.IsFullSnapshot);
        Assert.Equal(["Meeting"], first.Upserts.Select(e => e.Title));
        Assert.False(second.IsFullSnapshot);
        await Assert.ThrowsAsync<SyncStateExpiredException>(() =>
            source.SyncEventsAsync(collection, new SyncState("bogus"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Collections_are_listed()
    {
        var collections = await TaskSource().GetCollectionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["c", "other"], collections.Select(c => c.Id));
    }

    [Fact]
    public void A_source_can_serve_both_events_and_tasks_through_the_interfaces()
    {
        object source = CalendarSource();

        Assert.IsAssignableFrom<ICalendarSource>(source);
        Assert.IsNotAssignableFrom<ITaskSource>(source);
        Assert.Equal("in-memory", ((ISource)source).Kind);
    }

    [Fact]
    public void Sync_state_does_not_print_its_token() =>
        Assert.DoesNotContain("secret", new SyncState("secret").ToString(), StringComparison.Ordinal);

    [Fact]
    public void Sync_result_defaults_to_no_warnings() =>
        Assert.Empty(SyncResult.Snapshot<TaskItem>([], new SyncState("x")).Warnings);
}
