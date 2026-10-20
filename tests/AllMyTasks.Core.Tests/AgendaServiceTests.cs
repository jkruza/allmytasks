using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public class AgendaServiceTests
{
    private static readonly DateOnly Mon = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = Utc_(2026, 10, 19, 12);

    private static CalendarEvent Timed(string id, DateTimeOffset start, int minutes, Recurrence? recurrence = null, string collection = "c") =>
        new(new ItemKey("s", collection, id), id, new TimedEvent(start, start.AddMinutes(minutes)), Recurrence: recurrence);

    [Fact]
    public async Task Returns_events_in_range_sorted_all_day_first()
    {
        var (_, service) = await ServiceAsync(
            [
                Timed("late", Utc_(2026, 10, 19, 15), 60),
                Timed("early", Utc_(2026, 10, 19, 9), 60),
                new CalendarEvent(new ItemKey("s", "c", "holiday"), "holiday", new AllDayEvent(Mon, Mon.AddDays(1))),
                Timed("tomorrow", Utc_(2026, 10, 20, 9), 60),
            ],
            [],
            Now);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1)), TestContext.Current.CancellationToken);

        Assert.Equal(["holiday", "early", "late"], entries.Select(e => e.Title));
        Assert.Equal("#112233", entries[0].Color);
        Assert.Equal("Cal", entries[0].CollectionName);
    }

    [Fact]
    public async Task Expands_recurring_events_only_inside_the_range()
    {
        var start = Utc_(2026, 10, 12, 9);
        var rec = new Recurrence("FREQ=WEEKLY", [start.AddDays(7), start.AddDays(14), start.AddDays(21)], []);
        var (_, service) = await ServiceAsync([Timed("weekly", start, 30, rec)], [], Now);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(10)), TestContext.Current.CancellationToken);

        Assert.Equal([Utc_(2026, 10, 19, 9), Utc_(2026, 10, 26, 9)], entries.Select(e => e.Start));
    }

    [Fact]
    public async Task Override_replaces_occurrence_and_cancelled_override_removes_it()
    {
        var start = Utc_(2026, 10, 19, 9);
        var rec = new Recurrence("FREQ=WEEKLY", [start.AddDays(7), start.AddDays(14)], []);
        var master = Timed("weekly", start, 30, rec);
        var moved = new CalendarEvent(master.Key with { RecurrenceId = start }, "moved", new TimedEvent(start.AddHours(3), start.AddHours(4)));
        var cancelled = new CalendarEvent(master.Key with { RecurrenceId = start.AddDays(7) }, "gone", master.Time, Status: EventStatus.Cancelled);
        var (_, service) = await ServiceAsync([master, moved, cancelled], [], Now);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(30)), TestContext.Current.CancellationToken);

        Assert.Equal(["moved", "weekly"], entries.Select(e => e.Title));
        Assert.Equal(start.AddHours(3), entries[0].Start);
        Assert.Equal(start.AddDays(14), entries[1].Start);
    }

    [Fact]
    public async Task Hidden_collections_are_left_out()
    {
        var collections = new[]
        {
            new CollectionInfo("s", "c", "Shown", CollectionKind.Calendar),
            new CollectionInfo("s", "hidden", "Hidden", CollectionKind.Calendar, IsVisible: false),
        };
        var (store, service) = await ServiceAsync([Timed("a", Utc_(2026, 10, 19, 9), 30), Timed("b", Utc_(2026, 10, 19, 10), 30, collection: "hidden")], [], Now, collections);
        await store.SetCollectionPreferencesAsync("s", "hidden", false, null, TestContext.Current.CancellationToken);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1)), TestContext.Current.CancellationToken);

        Assert.Equal(["a"], entries.Select(e => e.Title));
    }

    [Fact]
    public async Task Event_crossing_midnight_covers_both_days()
    {
        var (_, service) = await ServiceAsync([Timed("late", Utc_(2026, 10, 19, 22), 240)], [], Now);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(2)), TestContext.Current.CancellationToken);

        Assert.Equal(Mon, entries[0].FirstDay);
        Assert.Equal(Mon.AddDays(2), entries[0].LastDayExclusive);
        Assert.True(entries[0].IsMultiDay);
    }

    [Fact]
    public async Task Event_ending_exactly_at_midnight_does_not_touch_the_next_day()
    {
        var (_, service) = await ServiceAsync([Timed("ends", Utc_(2026, 10, 19, 23), 60)], [], Now);

        var entries = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(2)), TestContext.Current.CancellationToken);

        Assert.Equal(Mon.AddDays(1), entries[0].LastDayExclusive);
    }

    [Fact]
    public async Task All_day_event_stays_on_its_date_in_every_zone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var (_, service) = await ServiceAsync(
            [new CalendarEvent(new ItemKey("s", "c", "xmas"), "xmas", new AllDayEvent(new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26)))],
            [],
            Now);

        foreach (var zone in new[] { Utc, tokyo, TestData.Warsaw })
        {
            var entries = await service.GetEntriesAsync(Query(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 28), zone), TestContext.Current.CancellationToken);

            Assert.Equal(new DateOnly(2026, 12, 25), entries.Single().FirstDay);
        }
    }

    [Fact]
    public async Task Tasks_without_a_due_value_are_not_on_the_calendar_and_completed_ones_follow_the_filter()
    {
        var tasks = new[]
        {
            new TaskItem(new ItemKey("s", "c", "undated"), "undated"),
            new TaskItem(new ItemKey("s", "c", "open"), "open", Due: new DueDate(Mon)),
            new TaskItem(new ItemKey("s", "c", "done"), "done", Due: new DueDate(Mon), Status: TaskItemStatus.Completed),
        };
        var (_, service) = await ServiceAsync([], tasks, Now);

        var hidden = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1)), TestContext.Current.CancellationToken);
        var shown = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1), filter: new ViewFilter(ShowCompleted: true)), TestContext.Current.CancellationToken);

        Assert.Equal(["open"], hidden.Select(e => e.Title));
        Assert.Equal(["done", "open"], shown.Select(e => e.Title).Order());
    }

    [Fact]
    public async Task Filter_can_hide_events_or_tasks()
    {
        var (_, service) = await ServiceAsync(
            [Timed("event", Utc_(2026, 10, 19, 9), 30)],
            [new TaskItem(new ItemKey("s", "c", "t"), "task", Due: new DueDate(Mon))],
            Now);

        var onlyEvents = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1), filter: new ViewFilter(ShowTasks: false)), TestContext.Current.CancellationToken);
        var onlyTasks = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1), filter: new ViewFilter(ShowEvents: false)), TestContext.Current.CancellationToken);

        Assert.IsType<EventEntry>(Assert.Single(onlyEvents));
        Assert.IsType<TaskEntry>(Assert.Single(onlyTasks));
    }

    [Fact]
    public async Task Overdue_tasks_before_the_range_are_returned_only_when_asked()
    {
        var task = new TaskItem(new ItemKey("s", "c", "old"), "old", Due: new DueDate(Mon.AddDays(-3)));
        var (_, service) = await ServiceAsync([], [task], Now);

        var without = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1)), TestContext.Current.CancellationToken);
        var with = await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1), overdue: true), TestContext.Current.CancellationToken);

        Assert.Empty(without);
        Assert.True(Assert.IsType<TaskEntry>(Assert.Single(with)).IsOverdue);
    }

    [Fact]
    public async Task Timed_task_is_a_point_on_its_due_day()
    {
        var task = new TaskItem(new ItemKey("s", "c", "t"), "t", Due: new DueDateTime(Utc_(2026, 10, 19, 17)));
        var (_, service) = await ServiceAsync([], [task], Utc_(2026, 10, 19, 8));

        var entry = Assert.IsType<TaskEntry>(Assert.Single(await service.GetEntriesAsync(Query(Mon, Mon.AddDays(1)), TestContext.Current.CancellationToken)));

        Assert.False(entry.IsAllDay);
        Assert.Equal(entry.Start, entry.End);
        Assert.False(entry.IsOverdue);
    }
}
