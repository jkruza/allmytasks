using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

/// <summary>A clock that only moves when told to.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

internal static class ViewTestKit
{
    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public static DateTimeOffset Utc_(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    public static EventEntry Event(string title, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo? zone = null)
    {
        zone ??= Utc;
        var (first, last) = AgendaService_DayRange(start, end, zone);
        var ev = new CalendarEvent(TestData.Key(title), title, new TimedEvent(start, end));
        return new EventEntry(new EventOccurrence(ev, ev.Time), "#123456", "Cal", start, end, false, first, last);
    }

    public static EventEntry AllDay(string title, DateOnly first, DateOnly lastExclusive, TimeZoneInfo? zone = null)
    {
        zone ??= Utc;
        var time = new AllDayEvent(first, lastExclusive);
        var ev = new CalendarEvent(TestData.Key(title), title, time);
        return new EventEntry(new EventOccurrence(ev, time), null, "Cal", time.StartInstant(zone), time.EndInstant(zone), true, first, lastExclusive);
    }

    public static TaskEntry Task(string title, DateOnly day, bool overdue = false, TimeZoneInfo? zone = null)
    {
        zone ??= Utc;
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var item = new TaskItem(TestData.Key(title), title, Due: new DueDate(day));
        return new TaskEntry(item, null, "List", start, start.AddDays(1), true, day, day.AddDays(1), overdue);
    }

    public static TaskEntry TimedTask(string title, DateTimeOffset at, bool overdue = false)
    {
        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var item = new TaskItem(TestData.Key(title), title, Due: new DueDateTime(at));
        return new TaskEntry(item, null, "List", at, at, false, day, day.AddDays(1), overdue);
    }

    // Mirrors the production helper so tests can build entries without the service.
    private static (DateOnly, DateOnly) AgendaService_DayRange(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var endLocal = TimeZoneInfo.ConvertTime(end, zone);
        var last = DateOnly.FromDateTime(endLocal.DateTime);
        return end > start && endLocal.TimeOfDay == TimeSpan.Zero ? (first, last) : (first, last.AddDays(1));
    }

    public static async Task<(InMemoryStore Store, AgendaService Service)> ServiceAsync(
        IEnumerable<CalendarEvent> events,
        IEnumerable<TaskItem> tasks,
        DateTimeOffset now,
        IEnumerable<CollectionInfo>? collections = null)
    {
        var store = new InMemoryStore();
        var info = new SourceInfo("s", "test", "Test");
        await store.SaveCollectionsAsync(
            info,
            [.. collections ?? [new CollectionInfo("s", "c", "Cal", CollectionKind.Calendar, "#112233")]],
            TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "c", AllMyTasks.Core.Providers.SyncResult.Snapshot<CalendarEvent>([.. events], new AllMyTasks.Core.Providers.SyncState("x")), TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "c", AllMyTasks.Core.Providers.SyncResult.Snapshot<TaskItem>([.. tasks], new AllMyTasks.Core.Providers.SyncState("x")), TestContext.Current.CancellationToken);
        return (store, new AgendaService(store, store, new FakeRecurrenceExpander(), new FixedClock(now)));
    }

    public static ViewQuery Query(DateOnly from, DateOnly toExclusive, TimeZoneInfo? zone = null, ViewFilter? filter = null, bool overdue = false) =>
        new(from, toExclusive, zone ?? Utc, filter ?? new ViewFilter(), overdue);
}
