using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.ICal.Tests;

public class MapperTests
{
    private static IcalParseResult Parse(string body, string header = "") => IcalMapper.Parse(Fixtures.Wrap(body, header), "src", "feed");

    private static CalendarEvent By(IcalParseResult r, string uid, bool overrides = false) =>
        r.Events.Single(e => e.Key.ItemId == uid && e.Key.IsOverride == overrides);

    [Fact]
    public void Utc_event_maps_every_field()
    {
        var e = By(Parse(Fixtures.Basic), "utc-1");

        var time = Assert.IsType<TimedEvent>(e.Time);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), time.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero), time.End);
        Assert.Equal("UTC", time.TimeZoneId);
        Assert.False(time.IsFloating);
        Assert.Equal("UTC meeting", e.Title);
        Assert.Equal("Bring notes, slides; and coffee", e.Description);
        Assert.Equal("Room 4", e.Location);
        Assert.Equal(new Uri("https://example.com/m"), e.Url);
        Assert.Equal(["work", "team"], e.Categories);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), e.LastModified);
        Assert.Equal(("src", "feed"), (e.Key.SourceId, e.Key.CollectionId));
    }

    [Fact]
    public void Tzid_event_keeps_the_iana_zone_and_the_right_instant()
    {
        var e = By(Parse(Fixtures.Basic), "tz-1");

        var time = Assert.IsType<TimedEvent>(e.Time);
        Assert.Equal("Europe/Warsaw", time.TimeZoneId);
        // 10:00 in Warsaw on 19 October is UTC+2.
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), time.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(90), time.End - time.Start);
        Assert.Equal(Availability.Free, e.Availability);
        Assert.Equal(EventStatus.Tentative, e.Status);
    }

    [Fact]
    public void All_day_events_use_dates_with_exclusive_end()
    {
        var r = Parse(Fixtures.Basic);

        Assert.Equal(new AllDayEvent(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 27)), By(r, "allday-1").Time);
        Assert.Equal(new AllDayEvent(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)), By(r, "allday-2").Time);
        Assert.Equal(new AllDayEvent(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 13)), By(r, "allday-3").Time);
    }

    [Fact]
    public void Floating_events_are_flagged_and_keep_their_wall_clock()
    {
        var time = Assert.IsType<TimedEvent>(By(Parse(Fixtures.Basic), "float-1").Time);

        Assert.True(time.IsFloating);
        Assert.Null(time.TimeZoneId);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), time.Start.DateTime);
        Assert.Equal(TimeSpan.FromMinutes(15), time.End - time.Start);
    }

    [Fact]
    public void Duration_and_missing_end_are_handled()
    {
        var r = Parse(Fixtures.Basic);

        var dur = Assert.IsType<TimedEvent>(By(r, "dur-1").Time);
        Assert.Equal(TimeSpan.FromMinutes(90), dur.End - dur.Start);
        var none = Assert.IsType<TimedEvent>(By(r, "noend-1").Time);
        Assert.Equal(none.Start, none.End);
    }

    [Fact]
    public void Cancelled_status_is_kept_not_dropped() =>
        Assert.Equal(EventStatus.Cancelled, By(Parse(Fixtures.Basic), "cancelled-1").Status);

    [Fact]
    public void Calendar_name_is_read()
    {
        var r = Parse(Fixtures.Basic, "X-WR-CALNAME:My feed\r\n");

        Assert.Equal("My feed", r.CalendarName);
    }

    [Fact]
    public void Recurring_master_carries_rule_exdates_and_rdates_in_the_common_model()
    {
        var r = Parse(Fixtures.Recurring);

        var master = By(r, "weekly-1");
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", master.Recurrence!.Rule);
        Assert.Equal([new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)], master.Recurrence.ExcludedDates.Select(d => d.ToUniversalTime()));
        Assert.Equal([new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero)], master.Recurrence.ExtraDates.Select(d => d.ToUniversalTime()));
    }

    [Fact]
    public void Overrides_are_linked_to_the_master_by_uid_and_recurrence_id()
    {
        var r = Parse(Fixtures.Recurring);

        var moved = r.Events.Single(e => e.Key.IsOverride && e.Title == "Weekly sync (moved)");
        Assert.Equal("weekly-1", moved.Key.ItemId);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), moved.Key.RecurrenceId!.Value.ToUniversalTime());
        Assert.Null(moved.Recurrence);
        Assert.Equal(3, r.Events.Count(e => e.Key.ItemId == "weekly-1"));
        Assert.Single(r.Events, e => e.Key.IsOverride && e.Status == EventStatus.Cancelled);
    }

    [Fact]
    public void All_day_recurrence_id_is_midnight_utc_of_the_date()
    {
        var text = Fixtures.Wrap("""
            BEGIN:VEVENT
            UID:y
            DTSTAMP:20261001T000000Z
            DTSTART;VALUE=DATE:20261224
            RRULE:FREQ=YEARLY
            SUMMARY:Y
            END:VEVENT
            BEGIN:VEVENT
            UID:y
            DTSTAMP:20261001T000000Z
            RECURRENCE-ID;VALUE=DATE:20271224
            DTSTART;VALUE=DATE:20271225
            SUMMARY:Y moved
            END:VEVENT
            """);

        var over = IcalMapper.Parse(text, "s", "c").Events.Single(e => e.Key.IsOverride);

        Assert.Equal(new DateTimeOffset(2027, 12, 24, 0, 0, 0, TimeSpan.Zero), over.Key.RecurrenceId);
    }

    [Fact]
    public void Windows_zone_names_are_mapped_to_iana_and_unknown_zones_fall_back_to_utc_with_a_warning()
    {
        var r = Parse(Fixtures.WindowsZones);

        var win = Assert.IsType<TimedEvent>(By(r, "win-1").Time);
        // "W. Europe Standard Time" is Windows' name for central Europe; .NET maps it to Europe/Berlin, which has the same rules.
        Assert.Equal("Europe/Berlin", win.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), win.Start.ToUniversalTime());
        var east = Assert.IsType<TimedEvent>(By(r, "win-2").Time);
        Assert.Equal("America/New_York", east.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero), east.Start.ToUniversalTime());
        var unknown = Assert.IsType<TimedEvent>(By(r, "unknown-1").Time);
        Assert.Equal("UTC", unknown.TimeZoneId);
        Assert.Contains(r.Warnings, w => w.ItemId == "unknown-1");
    }

    [Fact]
    public void Missing_uid_gets_a_stable_derived_id_and_a_warning()
    {
        var a = Parse(Fixtures.NoUid);
        var b = Parse(Fixtures.NoUid);

        var id = Assert.Single(a.Events).Key.ItemId;
        Assert.StartsWith("noid-", id, StringComparison.Ordinal);
        Assert.Equal(id, Assert.Single(b.Events).Key.ItemId);
        Assert.NotEmpty(a.Warnings);
    }

    [Fact]
    public void One_bad_component_does_not_cost_the_rest_of_the_feed()
    {
        var r = Parse(Fixtures.Malformed);

        Assert.Contains(r.Events, e => e.Key.ItemId == "good-1");
        Assert.Contains(r.Events, e => e.Key.ItemId == "good-2");
        Assert.DoesNotContain(r.Events, e => e.Key.ItemId is "bad-1" or "nostart-1");
        Assert.NotEmpty(r.Warnings);
        Assert.All(r.Warnings, w => Assert.DoesNotContain("Bad one", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Bom_lf_line_endings_folding_and_escapes_are_tolerated()
    {
        var text = "﻿BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:fold-1\nDTSTAMP:20261001T000000Z\nDTSTART:20261019T090000Z\nDTEND:20261019T100000Z\nSUMMARY:A very long title that has been fol\n ded across two lines\nDESCRIPTION:Line one\\nLine two\nEND:VEVENT\nEND:VCALENDAR\n";

        var e = Assert.Single(IcalMapper.Parse(text, "s", "c").Events);

        Assert.Equal("A very long title that has been folded across two lines", e.Title);
        Assert.Equal("Line one\nLine two", e.Description);
    }

    [Fact]
    public void Very_long_text_is_kept_intact()
    {
        var long_ = new string('x', 5000);
        var text = Fixtures.Wrap($"BEGIN:VEVENT\nUID:l\nDTSTAMP:20261001T000000Z\nDTSTART:20261019T090000Z\nSUMMARY:{long_}\nEND:VEVENT");

        Assert.Equal(long_, Assert.Single(IcalMapper.Parse(text, "s", "c").Events).Title);
    }

    [Fact]
    public void Non_calendar_text_is_a_data_error_not_an_empty_calendar()
    {
        Assert.Throws<SourceDataException>(() => IcalMapper.Parse("<html><body>Not found</body></html>", "s", "c"));
        Assert.False(IcalMapper.LooksLikeCalendar("<html>"));
        Assert.True(IcalMapper.LooksLikeCalendar("﻿  begin:vcalendar\r\n"));
    }

    [Fact]
    public void A_valid_calendar_without_events_is_empty()
    {
        var r = IcalMapper.Parse(Fixtures.Wrap(string.Empty), "s", "c");

        Assert.Empty(r.Events);
    }
}
