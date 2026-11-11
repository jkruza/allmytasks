using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Time;
using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public sealed class WorkloadSummaryTests
{
    private static readonly DateOnly TodayDate = new(2026, 11, 11);

    private static WorkloadSummary Build(IEnumerable<AgendaEntry> entries, WorkloadOptions? options = null, TimeZoneInfo? zone = null, DateOnly? today = null, int days = 7) =>
        WorkloadSummary.Build(entries, today ?? TodayDate, zone ?? Utc, options ?? WorkloadOptions.Default, days);

    private static EventEntry Meeting(string title, int hour, double hours, Availability availability = Availability.Busy, EventStatus status = EventStatus.Confirmed, int day = 11)
    {
        var start = Utc_(2026, 11, day, hour);
        var end = start.AddHours(hours);
        var time = new TimedEvent(start, end);
        var ev = new CalendarEvent(TestData.Key(title), title, time, Availability: availability, Status: status);
        var first = DateOnly.FromDateTime(start.UtcDateTime);
        return new EventEntry(new EventOccurrence(ev, time), null, "Cal", start, end, false, first, first.AddDays(1));
    }

    private static TaskEntry Due(string title, DateOnly day, bool overdue = false, TaskItemStatus status = TaskItemStatus.Open)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var item = new TaskItem(TestData.Key(title), title, Due: new DueDate(day), Status: status);
        return new TaskEntry(item, null, "List", start, start.AddDays(1), true, day, day.AddDays(1), overdue);
    }

    [Fact]
    public void An_empty_week_has_seven_quiet_days()
    {
        var summary = Build([]);

        Assert.Equal(7, summary.Days.Count);
        Assert.Equal(TodayDate, summary.Days[0].Date);
        Assert.All(summary.Days, d => Assert.Equal((TimeSpan.Zero, 0, false), (d.MeetingTime, d.TasksDue, d.IsHeavy)));
        Assert.Equal(0, summary.Overdue);
    }

    [Fact]
    public void Meeting_time_adds_up_per_day()
    {
        var summary = Build([Meeting("a", 9, 1), Meeting("b", 13, 1.5), Meeting("c", 10, 2, day: 12)]);

        Assert.Equal(TimeSpan.FromHours(2.5), summary.Days[0].MeetingTime);
        Assert.Equal(TimeSpan.FromHours(2), summary.Days[1].MeetingTime);
    }

    [Fact]
    public void Overlapping_meetings_count_once()
    {
        var summary = Build([Meeting("a", 9, 2), Meeting("b", 10, 2), Meeting("inside", 9, 1)]);

        Assert.Equal(TimeSpan.FromHours(3), summary.Days[0].MeetingTime);
    }

    [Fact]
    public void All_day_free_and_cancelled_events_are_not_meeting_time()
    {
        var summary = Build([AllDay("holiday", TodayDate, TodayDate.AddDays(1)), Meeting("free", 9, 1, Availability.Free), Meeting("declined", 11, 1, status: EventStatus.Cancelled), Meeting("tentative", 14, 1, status: EventStatus.Tentative)]);

        Assert.Equal(TimeSpan.FromHours(1), summary.Days[0].MeetingTime);
    }

    [Fact]
    public void An_event_across_midnight_is_split_between_the_days()
    {
        var summary = Build([Meeting("late", 22, 4)]);

        Assert.Equal(TimeSpan.FromHours(2), summary.Days[0].MeetingTime);
        Assert.Equal(TimeSpan.FromHours(2), summary.Days[1].MeetingTime);
    }

    [Fact]
    public void A_day_is_its_real_length_on_a_daylight_saving_change()
    {
        // Europe/Warsaw ends summer time on 25 October 2026, so that day has 25 hours.
        var day = new DateOnly(2026, 10, 25);
        var start = day.StartOfDay(TestData.Warsaw);
        var end = day.AddDays(1).StartOfDay(TestData.Warsaw);
        var time = new TimedEvent(start, end);
        var ev = new CalendarEvent(TestData.Key("all day long"), "all day long", time);
        var entry = new EventEntry(new EventOccurrence(ev, time), null, "Cal", start, end, false, day, day.AddDays(1));

        var summary = Build([entry], zone: TestData.Warsaw, today: day, days: 2);

        Assert.Equal(TimeSpan.FromHours(25), summary.Days[0].MeetingTime);
        Assert.Equal(TimeSpan.Zero, summary.Days[1].MeetingTime);
    }

    [Fact]
    public void Tasks_due_count_open_tasks_on_their_day_only()
    {
        var summary = Build([Due("a", TodayDate), Due("b", TodayDate), Due("later", TodayDate.AddDays(2)), Due("done", TodayDate, status: TaskItemStatus.Completed), Due("late", TodayDate.AddDays(-1), overdue: true)]);

        Assert.Equal([2, 0, 1, 0, 0, 0, 0], summary.Days.Select(d => d.TasksDue));
        Assert.Equal(1, summary.Overdue);
    }

    [Fact]
    public void Completed_overdue_tasks_are_not_counted_as_overdue()
    {
        Assert.Equal(0, Build([Due("done", TodayDate.AddDays(-3), overdue: true, status: TaskItemStatus.Completed)]).Overdue);
    }

    [Fact]
    public void A_day_is_heavy_at_either_limit()
    {
        var options = new WorkloadOptions(TimeSpan.FromHours(6), 3);
        var summary = Build([Meeting("long", 8, 6), Due("a", TodayDate.AddDays(1)), Due("b", TodayDate.AddDays(1)), Due("c", TodayDate.AddDays(1)), Meeting("short", 8, 5.9, day: 13), Due("d", TodayDate.AddDays(2)), Due("e", TodayDate.AddDays(2))], options);

        Assert.Equal([true, true, false, false, false, false, false], summary.Days.Select(d => d.IsHeavy));
    }

    [Fact]
    public void Entries_outside_the_range_are_ignored()
    {
        var summary = Build([Meeting("past", 9, 1, day: 9), Meeting("far", 9, 1, day: 30), Due("far", TodayDate.AddDays(20))], days: 3);

        Assert.Equal(3, summary.Days.Count);
        Assert.All(summary.Days, d => Assert.Equal((TimeSpan.Zero, 0), (d.MeetingTime, d.TasksDue)));
    }

    [Fact]
    public void Settings_default_and_round_trip_and_reject_out_of_range_values()
    {
        var defaults = new AppSettings();
        Assert.True(defaults.ShowWorkload);
        Assert.Equal(WorkloadOptions.Default, defaults.ToWorkloadOptions());

        var changed = AppSettings.Parse((defaults with { ShowWorkload = false, WorkloadMeetingHours = 4, WorkloadTasks = 12 }).ToJson());
        Assert.False(changed.ShowWorkload);
        Assert.Equal(new WorkloadOptions(TimeSpan.FromHours(4), 12), changed.ToWorkloadOptions());

        var bad = AppSettings.Parse("""{"workloadMeetingHours":0,"workloadTasks":500}""");
        Assert.Equal(WorkloadOptions.Default, bad.ToWorkloadOptions());
    }
}
