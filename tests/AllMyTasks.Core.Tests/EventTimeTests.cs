using AllMyTasks.Core.Calendar;

namespace AllMyTasks.Core.Tests;

public class EventTimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TimedEvent_allows_zero_length() =>
        Assert.Equal(T0, new TimedEvent(T0, T0).End);

    [Fact]
    public void TimedEvent_rejects_end_before_start() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedEvent(T0, T0.AddMinutes(-1)));

    [Fact]
    public void AllDayEvent_requires_end_after_start()
    {
        var day = new DateOnly(2026, 12, 25);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AllDayEvent(day, day));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AllDayEvent(day, day.AddDays(-1)));
    }

    [Fact]
    public void AllDayEvent_counts_days_with_exclusive_end() =>
        Assert.Equal(3, new AllDayEvent(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 27)).Days);

    [Fact]
    public void AllDayEvent_is_on_the_same_date_in_every_zone()
    {
        var christmas = new AllDayEvent(new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26));
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        Assert.Equal(new DateTime(2026, 12, 25), christmas.StartInstant(TestData.Warsaw).DateTime);
        Assert.Equal(new DateTime(2026, 12, 25), christmas.StartInstant(tokyo).DateTime);
        Assert.Equal(new DateTime(2026, 12, 26), christmas.EndInstant(tokyo).DateTime);
    }

    [Fact]
    public void AllDayEvent_spanning_spring_forward_is_one_hour_shorter()
    {
        // Europe/Warsaw springs forward on 2026-03-29, so that day has 23 hours.
        var day = new AllDayEvent(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 30));

        Assert.Equal(TimeSpan.FromHours(23), day.EndInstant(TestData.Warsaw) - day.StartInstant(TestData.Warsaw));
    }

    [Fact]
    public void AllDayEvent_spanning_fall_back_is_one_hour_longer()
    {
        var day = new AllDayEvent(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26));

        Assert.Equal(TimeSpan.FromHours(25), day.EndInstant(TestData.Warsaw) - day.StartInstant(TestData.Warsaw));
    }

    [Fact]
    public void TimedEvent_keeps_its_instant_in_any_display_zone()
    {
        var timed = new TimedEvent(T0, T0.AddHours(1), "Europe/Warsaw");

        Assert.Equal(T0, timed.StartInstant(TestData.Warsaw));
        Assert.Equal(T0, timed.StartInstant(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Floating_time_takes_the_wall_clock_in_the_display_zone()
    {
        var floating = new TimedEvent(T0, T0.AddHours(1), IsFloating: true);

        // 10:00 wall clock in Warsaw in October (UTC+2) is 08:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), floating.StartInstant(TestData.Warsaw));
        Assert.Equal(T0, floating.StartInstant(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Overlaps_uses_half_open_ranges()
    {
        var timed = new TimedEvent(T0, T0.AddHours(1));
        var zone = TimeZoneInfo.Utc;

        Assert.True(timed.Overlaps(T0.AddMinutes(30), T0.AddHours(2), zone));
        Assert.False(timed.Overlaps(T0.AddHours(1), T0.AddHours(2), zone));
        Assert.False(timed.Overlaps(T0.AddHours(-1), T0, zone));
    }

    [Fact]
    public void Overlaps_treats_zero_length_event_as_a_point()
    {
        var point = new TimedEvent(T0, T0);

        Assert.True(point.Overlaps(T0, T0.AddHours(1), TimeZoneInfo.Utc));
        Assert.False(point.Overlaps(T0.AddHours(-1), T0, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Calendar_event_defaults_and_title_invariant()
    {
        var time = new TimedEvent(T0, T0.AddHours(1));
        var ev = new CalendarEvent(TestData.Key(), string.Empty, time);

        Assert.Equal(string.Empty, ev.Title);
        Assert.Empty(ev.Categories);
        Assert.Equal(EventStatus.Confirmed, ev.Status);
        Assert.Equal(Availability.Busy, ev.Availability);
        Assert.Throws<ArgumentNullException>(() => new CalendarEvent(TestData.Key(), null!, time));
    }
}
