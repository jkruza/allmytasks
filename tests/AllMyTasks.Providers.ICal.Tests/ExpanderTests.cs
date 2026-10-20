using AllMyTasks.Core.Calendar;

namespace AllMyTasks.Providers.ICal.Tests;

public class ExpanderTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private readonly IcalRecurrenceExpander _expander = new();

    private static (CalendarEvent Master, List<CalendarEvent> Overrides) Load(string body, string uid)
    {
        var events = IcalMapper.Parse(Fixtures.Wrap(body), "s", "c").Events;
        return (events.Single(e => e.Key.ItemId == uid && !e.Key.IsOverride), [.. events.Where(e => e.Key.ItemId == uid && e.Key.IsOverride)]);
    }

    private IReadOnlyList<EventOccurrence> Expand(string uid, string body, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        var (master, overrides) = Load(body, uid);
        return _expander.Expand(master, overrides, from, to, zone ?? TimeZoneInfo.Utc);
    }

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Weekly_rule_with_exdate_rdate_moved_and_cancelled_occurrences()
    {
        var found = Expand("weekly-1", Fixtures.Recurring, Utc(2026, 10, 1), Utc(2026, 12, 1));

        var summary = found.Select(o => (o.Time.StartInstant(TimeZoneInfo.Utc), o.Event.Title)).ToList();
        // Mondays 09:00 Warsaw (UTC+2 until 25 Oct, then UTC+1): 19 Oct, 26 Oct (moved to 14:00), 2 Nov (EXDATE), 9 Nov (cancelled), 16, 23, 30 Nov; plus RDATE 28 Oct 15:00.
        Assert.Equal(
        [
            (Utc(2026, 10, 19, 7), "Weekly sync"),
            (Utc(2026, 10, 26, 13), "Weekly sync (moved)"),
            (Utc(2026, 10, 28, 14), "Weekly sync"),
            (Utc(2026, 11, 16, 8), "Weekly sync"),
            (Utc(2026, 11, 23, 8), "Weekly sync"),
            (Utc(2026, 11, 30, 8), "Weekly sync"),
        ],
        summary);
        Assert.Single(found, o => o.IsOverride);
    }

    [Fact]
    public void Wall_clock_time_stays_put_across_the_dst_change()
    {
        var found = Expand("weekly-1", Fixtures.Recurring, Utc(2026, 10, 15), Utc(2026, 11, 30), Warsaw);

        var mondays = found.Where(o => !o.IsOverride && o.Time.StartInstant(Warsaw).DayOfWeek == DayOfWeek.Monday).Select(o => o.Time.StartInstant(Warsaw).Hour).Distinct();
        Assert.Equal([9], mondays);
    }

    [Fact]
    public void Infinite_series_only_expand_inside_the_range_and_terminate()
    {
        var found = Expand("weekly-1", Fixtures.Recurring, Utc(2030, 1, 1), Utc(2030, 1, 15));

        Assert.NotEmpty(found);
        Assert.All(found, o => Assert.True(o.Time.StartInstant(TimeZoneInfo.Utc).Year == 2030));
        Assert.True(found.Count <= 3);
    }

    [Fact]
    public void Count_and_until_end_the_series()
    {
        Assert.Equal(3, Expand("daily-utc", Fixtures.Recurring, Utc(2026, 10, 1), Utc(2027, 10, 1)).Count);
        Assert.Equal(4, Expand("until-1", Fixtures.Recurring, Utc(2026, 10, 1), Utc(2027, 10, 1)).Count);
    }

    [Fact]
    public void All_day_yearly_series_stays_on_its_date()
    {
        var found = Expand("yearly-1", Fixtures.Recurring, Utc(2026, 1, 1), Utc(2029, 1, 1), Warsaw);

        Assert.Equal(
            [new DateOnly(2026, 12, 24), new DateOnly(2027, 12, 24), new DateOnly(2028, 12, 24)],
            found.Select(o => ((AllDayEvent)o.Time).Start));
    }

    [Fact]
    public void Occurrence_keeps_the_masters_duration()
    {
        var found = Expand("weekly-1", Fixtures.Recurring, Utc(2026, 11, 15), Utc(2026, 11, 20));

        var t = Assert.IsType<TimedEvent>(Assert.Single(found).Time);
        Assert.Equal(TimeSpan.FromMinutes(30), t.End - t.Start);
    }

    [Fact]
    public void A_runaway_series_is_capped_per_query()
    {
        var body = """
            BEGIN:VEVENT
            UID:minutely
            DTSTAMP:20261001T000000Z
            DTSTART:20200101T000000Z
            DTEND:20200101T000100Z
            RRULE:FREQ=MINUTELY
            SUMMARY:Spam
            END:VEVENT
            """;

        var found = Expand("minutely", body, Utc(2026, 1, 1), Utc(2026, 12, 31));

        // Whatever it yields, it must stop: no hang, and never more than the cap.
        Assert.True(found.Count <= RecurrenceExpanderBase.MaxOccurrencesPerSeries);
    }

    [Fact]
    public void A_long_running_daily_series_reaches_a_range_years_after_its_start()
    {
        var body = """
            BEGIN:VEVENT
            UID:old
            DTSTAMP:20261001T000000Z
            DTSTART:20150101T090000Z
            DTEND:20150101T093000Z
            RRULE:FREQ=DAILY
            SUMMARY:Old daily
            END:VEVENT
            """;

        var found = Expand("old", body, Utc(2026, 10, 19), Utc(2026, 10, 22));

        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void A_single_event_expands_to_itself_only_when_in_range()
    {
        var body = """
            BEGIN:VEVENT
            UID:one
            DTSTAMP:20261001T000000Z
            DTSTART:20261019T090000Z
            DTEND:20261019T100000Z
            SUMMARY:One
            END:VEVENT
            """;

        Assert.Single(Expand("one", body, Utc(2026, 10, 19), Utc(2026, 10, 20)));
        Assert.Empty(Expand("one", body, Utc(2026, 10, 20), Utc(2026, 10, 21)));
    }
}
