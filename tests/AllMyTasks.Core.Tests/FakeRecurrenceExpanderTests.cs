using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Tests;

public class FakeRecurrenceExpanderTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Mon = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mon2 = Mon.AddDays(7);
    private static readonly DateTimeOffset Mon3 = Mon.AddDays(14);

    private readonly FakeRecurrenceExpander _expander = new();

    private static CalendarEvent Series(Recurrence? recurrence) =>
        new(TestData.Key("uid"), "Standup", new TimedEvent(Mon, Mon.AddMinutes(30)), Recurrence: recurrence);

    private static CalendarEvent Override(DateTimeOffset original, EventTime time, EventStatus status = EventStatus.Confirmed) =>
        new(TestData.Key("uid") with { RecurrenceId = original }, "Standup (moved)", time, Status: status);

    private IReadOnlyList<EventOccurrence> Expand(CalendarEvent master, IEnumerable<CalendarEvent>? overrides = null, int days = 30) =>
        _expander.Expand(master, overrides ?? [], Mon.AddDays(-1), Mon.AddDays(days), Utc);

    [Fact]
    public void Single_event_yields_itself_once()
    {
        var occurrences = Expand(Series(null));

        Assert.Single(occurrences);
        Assert.False(occurrences[0].IsOverride);
    }

    [Fact]
    public void Extra_dates_become_occurrences_with_the_same_duration()
    {
        var occurrences = Expand(Series(new Recurrence("FREQ=WEEKLY", [Mon2, Mon3], [])));

        Assert.Equal([Mon, Mon2, Mon3], occurrences.Select(o => o.Time.StartInstant(Utc)));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromMinutes(30), o.Time.EndInstant(Utc) - o.Time.StartInstant(Utc)));
    }

    [Fact]
    public void Excluded_dates_are_skipped()
    {
        var occurrences = Expand(Series(new Recurrence("FREQ=WEEKLY", [Mon2, Mon3], [Mon2])));

        Assert.Equal([Mon, Mon3], occurrences.Select(o => o.Time.StartInstant(Utc)));
    }

    [Fact]
    public void Override_replaces_the_generated_occurrence()
    {
        var moved = new TimedEvent(Mon2.AddHours(2), Mon2.AddHours(3));
        var occurrences = Expand(Series(new Recurrence("FREQ=WEEKLY", [Mon2], [])), [Override(Mon2, moved)]);

        Assert.Equal(2, occurrences.Count);
        Assert.True(occurrences[1].IsOverride);
        Assert.Equal(moved, occurrences[1].Time);
        Assert.Equal("Standup (moved)", occurrences[1].Event.Title);
    }

    [Fact]
    public void Cancelled_override_removes_the_occurrence()
    {
        var occurrences = Expand(
            Series(new Recurrence("FREQ=WEEKLY", [Mon2], [])),
            [Override(Mon2, new TimedEvent(Mon2, Mon2.AddMinutes(30)), EventStatus.Cancelled)]);

        Assert.Equal([Mon], occurrences.Select(o => o.Time.StartInstant(Utc)));
    }

    [Fact]
    public void Only_occurrences_in_the_range_are_returned()
    {
        var master = Series(new Recurrence("FREQ=WEEKLY", [Mon2, Mon3], []));

        var occurrences = _expander.Expand(master, [], Mon2, Mon3, Utc);

        Assert.Equal([Mon2], occurrences.Select(o => o.Time.StartInstant(Utc)));
    }

    [Fact]
    public void Cancelled_master_yields_nothing()
    {
        var master = Series(null) with { Status = EventStatus.Cancelled };

        Assert.Empty(Expand(master));
    }

    [Fact]
    public void All_day_series_expands_by_date_and_matches_overrides_by_utc_midnight()
    {
        var first = new DateOnly(2026, 12, 24);
        var master = new CalendarEvent(
            TestData.Key("holiday"),
            "Holiday",
            new AllDayEvent(first, first.AddDays(1)),
            Recurrence: new Recurrence("FREQ=YEARLY", [new DateTimeOffset(2027, 12, 24, 0, 0, 0, TimeSpan.Zero)], []));
        var cancelled = new CalendarEvent(
            TestData.Key("holiday") with { RecurrenceId = new DateTimeOffset(2027, 12, 24, 0, 0, 0, TimeSpan.Zero) },
            "Holiday",
            new AllDayEvent(new DateOnly(2027, 12, 24), new DateOnly(2027, 12, 25)),
            Status: EventStatus.Cancelled);

        var range = _expander.Expand(master, [cancelled], new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero), TestData.Warsaw);

        Assert.Single(range);
        Assert.Equal(new AllDayEvent(first, first.AddDays(1)), range[0].Time);
    }

    [Fact]
    public void Results_are_ordered_by_start()
    {
        var master = Series(new Recurrence("FREQ=WEEKLY", [Mon3, Mon2], []));

        var starts = Expand(master).Select(o => o.Time.StartInstant(Utc)).ToList();

        Assert.Equal(starts.Order(), starts);
    }
}
