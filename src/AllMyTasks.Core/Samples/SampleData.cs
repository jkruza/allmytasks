using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Samples;

/// <summary>
/// Made-up events and tasks that deliberately include the awkward cases: overlaps, items crossing
/// midnight, multi-day and all-day events, a moved and a cancelled occurrence, other time zones, a
/// floating time, DST days, subtasks, and overdue and undated tasks. All dates are relative to "today".
/// </summary>
public static class SampleData
{
    // One source per provider instance: a source that offers both calendars and task lists would use one id.
    public const string WorkSourceId = "sample-work";
    public const string HomeSourceId = "sample-home";
    public const string WorkTasksSourceId = "sample-work-tasks";
    public const string HomeTasksSourceId = "sample-home-tasks";

    /// <summary>Creates a store filled through the normal sync path, as a real provider would fill it.</summary>
    public static async Task<InMemoryStore> CreateStoreAsync(DateOnly today, TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        var store = new InMemoryStore();
        var calendars = new List<ICalendarSource> { WorkCalendar(today, zone), PersonalCalendar(today, zone) };
        var taskSources = new List<ITaskSource> { WorkTasks(today, zone), HomeTasks(today, zone) };

        foreach (ISource source in calendars.Cast<ISource>().Concat(taskSources))
        {
            var info = new SourceInfo(source.Id, source.Kind, source.DisplayName);
            var collections = await source.GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveCollectionsAsync(info, collections, cancellationToken).ConfigureAwait(false);
            foreach (var collection in collections)
            {
                if (source is ICalendarSource calendar && collection.Kind == CollectionKind.Calendar)
                {
                    var result = await calendar.SyncEventsAsync(collection, null, cancellationToken).ConfigureAwait(false);
                    await store.ApplyAsync(collection.SourceId, collection.Id, result, cancellationToken).ConfigureAwait(false);
                }

                if (source is ITaskSource tasks && collection.Kind == CollectionKind.TaskList)
                {
                    var result = await tasks.SyncTasksAsync(collection, null, cancellationToken).ConfigureAwait(false);
                    await store.ApplyAsync(collection.SourceId, collection.Id, result, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return store;
    }

    public static InMemoryCalendarSource WorkCalendar(DateOnly today, TimeZoneInfo zone)
    {
        var key = (string id) => new ItemKey(WorkSourceId, "meetings", id);
        TimedEvent Timed(DateOnly day, int h, int m, int minutes, string? tz = null) =>
            new(At(day, h, m, zone), At(day, h, m, zone).AddMinutes(minutes), tz ?? zone.Id);

        var monday = WeekStart(today);
        var standupStart = At(monday, 9, 0, zone);
        var standup = new CalendarEvent(
            key("standup"),
            "Team stand-up",
            new TimedEvent(standupStart, standupStart.AddMinutes(15), zone.Id),
            Location: "Room 4",
            Recurrence: new Recurrence("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", Weekdays(monday, 8, zone, 9, 0), []));

        var events = new List<CalendarEvent>
        {
            standup,
            Override(standup, monday.AddDays(2), new TimedEvent(At(monday.AddDays(2), 10, 30, zone), At(monday.AddDays(2), 10, 45, zone), zone.Id), "Team stand-up (moved)", zone),
            Cancel(standup, monday.AddDays(3), zone),

            // An overlapping cluster, plus one that merely touches it.
            new(key("design"), "Design review", Timed(today, 10, 0, 90), Location: "Studio"),
            new(key("sync"), "Sync with Sam", Timed(today, 10, 30, 60)),
            new(key("lunch"), "Lunch & learn", Timed(today, 11, 0, 30), Status: EventStatus.Tentative),
            new(key("retro"), "Retro", Timed(today, 11, 30, 60), Location: "Room 2"),
            new(key("focus"), "Focus time", Timed(today, 14, 0, 120), Availability: Availability.Free),

            // Crosses midnight, in another time zone, and floating.
            new(key("oncall"), "On-call handover", Timed(today.AddDays(1), 22, 0, 240)),
            new(key("ny"), "Call with New York", new TimedEvent(At(today.AddDays(2), 9, 0, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), At(today.AddDays(2), 10, 0, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), "America/New_York")),
            new(key("float"), "Take medication", new TimedEvent(new DateTimeOffset(today.AddDays(1).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero), new DateTimeOffset(today.AddDays(1).ToDateTime(new TimeOnly(8, 15)), TimeSpan.Zero), null, IsFloating: true)),

            // Multi-day and all-day.
            new(key("conf"), "DevConf", new AllDayEvent(today.AddDays(2), today.AddDays(5)), Location: "Gdańsk"),
            new(key("offsite"), "Team offsite", new AllDayEvent(today.AddDays(-1), today.AddDays(1))),
            new(key("holiday"), "Public holiday", new AllDayEvent(today.AddDays(4), today.AddDays(5))),

            // The days Europe changes its clocks (2026): 23 and 25 hour days.
            new(key("dst-spring"), "Night shift (clocks go forward)", new TimedEvent(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2026, 3, 29, 5, 0, 0, TimeSpan.FromHours(2)), "Europe/Warsaw")),
            new(key("dst-autumn"), "Night shift (clocks go back)", new TimedEvent(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.FromHours(1)), "Europe/Warsaw")),
        };

        return new InMemoryCalendarSource(WorkSourceId, "Work", [new CollectionInfo(WorkSourceId, "meetings", "Meetings", CollectionKind.Calendar, "#3b82f6")], events);
    }

    public static InMemoryCalendarSource PersonalCalendar(DateOnly today, TimeZoneInfo zone)
    {
        var key = (string id) => new ItemKey(HomeSourceId, "personal", id);
        var events = new List<CalendarEvent>
        {
            new(key("birthday"), "Alex's birthday", new AllDayEvent(today.AddDays(3), today.AddDays(4))),
            new(key("dinner"), "Dinner out", new TimedEvent(At(today, 19, 0, zone), At(today, 21, 0, zone), zone.Id)),
            new(key("gym"), "Gym", new TimedEvent(At(today.AddDays(1), 7, 0, zone), At(today.AddDays(1), 8, 0, zone), zone.Id), Recurrence: new Recurrence("FREQ=WEEKLY", [At(today.AddDays(8), 7, 0, zone)], [])),
            new(key("untitled"), string.Empty, new TimedEvent(At(today.AddDays(5), 12, 0, zone), At(today.AddDays(5), 12, 0, zone), zone.Id)),
        };

        return new InMemoryCalendarSource(HomeSourceId, "Home", [new CollectionInfo(HomeSourceId, "personal", "Personal", CollectionKind.Calendar, "#10b981")], events);
    }

    public static InMemoryTaskSource WorkTasks(DateOnly today, TimeZoneInfo zone)
    {
        var key = (string id) => new ItemKey(WorkTasksSourceId, "todo", id);
        var tasks = new List<TaskItem>
        {
            new(key("report"), "Quarterly report", Notes: "Numbers from finance", Due: new DueDate(today.AddDays(-2)), Priority: Priority.High, Labels: ["finance"]),
            new(key("report-draft"), "Draft summary", Due: new DueDate(today.AddDays(-2)), ParentKey: key("report")),
            new(key("report-charts"), "Make charts", Due: new DueDate(today), ParentKey: key("report"), Status: TaskItemStatus.InProgress),
            new(key("review"), "Review pull request", Due: new DueDateTime(At(today, 16, 0, zone), zone.Id), Priority: Priority.Medium, Labels: ["code"]),
            new(key("earlier"), "Send invoice", Due: new DueDateTime(At(today, 0, 5, zone), zone.Id), Priority: Priority.Low),
            new(key("plan"), "Plan next sprint", Due: new DueDate(today.AddDays(1)), Labels: ["planning", "code"]),
            new(key("someday"), "Learn Avalonia animations", Notes: "Someday"),
            new(key("done"), "Book flights", Due: new DueDate(today.AddDays(-5)), Status: TaskItemStatus.Completed, CompletedAt: At(today.AddDays(-4), 12, 0, zone)),
            new(key("dropped"), "Migrate wiki", Status: TaskItemStatus.Cancelled),
            new(key("later"), "Renew certificate", Due: new DueDate(today.AddDays(30)), Priority: Priority.High, Labels: ["ops"]),
        };

        return new InMemoryTaskSource(WorkTasksSourceId, "Work tasks", [new CollectionInfo(WorkTasksSourceId, "todo", "Work tasks", CollectionKind.TaskList, "#f59e0b")], tasks);
    }

    public static InMemoryTaskSource HomeTasks(DateOnly today, TimeZoneInfo zone)
    {
        var key = (string id) => new ItemKey(HomeTasksSourceId, "chores", id);
        var tasks = new List<TaskItem>
        {
            new(key("milk"), "Buy milk", Due: new DueDate(today)),
            new(key("bike"), "Fix the bike", Due: new DueDate(today.AddDays(6)), Labels: ["weekend"]),
            new(key("call"), "Call the dentist", Due: new DueDateTime(At(today.AddDays(2), 9, 30, zone), zone.Id), Priority: Priority.Medium),
            new(key("garden"), "Tidy the garden"),
        };

        return new InMemoryTaskSource(HomeTasksSourceId, "Home tasks", [new CollectionInfo(HomeTasksSourceId, "chores", "Chores", CollectionKind.TaskList, "#ec4899")], tasks);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static List<DateTimeOffset> Weekdays(DateOnly from, int weeks, TimeZoneInfo zone, int hour, int minute)
    {
        var dates = new List<DateTimeOffset>();
        for (var i = 1; i < weeks * 7; i++)
        {
            var day = from.AddDays(i);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                dates.Add(At(day, hour, minute, zone));
            }
        }

        return dates;
    }

    private static CalendarEvent Override(CalendarEvent master, DateOnly originalDay, TimedEvent moved, string title, TimeZoneInfo zone) =>
        new(master.Key with { RecurrenceId = At(originalDay, 9, 0, zone) }, title, moved);

    private static CalendarEvent Cancel(CalendarEvent master, DateOnly originalDay, TimeZoneInfo zone)
    {
        var start = At(originalDay, 9, 0, zone);
        return new CalendarEvent(master.Key with { RecurrenceId = start }, master.Title, new TimedEvent(start, start.AddMinutes(15), zone.Id), Status: EventStatus.Cancelled);
    }
}
