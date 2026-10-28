using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Reminders;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public class ReminderPlannerTests
{
    private static readonly DateTimeOffset Noon = Utc_(2026, 10, 20, 12);
    private static readonly ReminderSettings Defaults = ReminderSettings.Default;

    private static IReadOnlyList<Reminder> Plan(IEnumerable<AgendaEntry> entries, ReminderSettings? settings = null, TimeZoneInfo? zone = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        ReminderPlanner.Plan(entries, settings ?? Defaults, zone ?? Utc, from ?? Noon.AddDays(-1), to ?? Noon.AddDays(2));

    [Fact]
    public void Timed_event_reminds_the_lead_time_before_the_start()
    {
        var reminder = Assert.Single(Plan([Event("Standup", Noon, Noon.AddMinutes(30))]));

        Assert.Equal(Noon.AddMinutes(-10), reminder.FireAt);
        Assert.Equal(ReminderKind.EventStart, reminder.Kind);
        Assert.Equal("Standup", reminder.Title);
        Assert.Equal(Noon, reminder.Occurrence);
        Assert.Equal(Noon.AddMinutes(30), reminder.EndsAt);
    }

    [Fact]
    public void Lead_time_comes_from_the_settings()
    {
        var reminder = Assert.Single(Plan([Event("A", Noon, Noon.AddHours(1))], Defaults with { EventLead = TimeSpan.Zero }));

        Assert.Equal(Noon, reminder.FireAt);
    }

    [Fact]
    public void Detail_has_the_start_time_and_the_location()
    {
        var entry = Event("A", Noon, Noon.AddHours(1));
        var withLocation = entry with { Occurrence = entry.Occurrence with { Event = entry.Occurrence.Event with { Location = "Room 3" } } };

        var detail = Assert.Single(Plan([withLocation])).Detail;

        Assert.Contains("Room 3", detail, StringComparison.Ordinal);
        Assert.Contains("12", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void All_day_and_cancelled_events_get_no_reminder()
    {
        var cancelled = Event("Gone", Noon, Noon.AddHours(1));
        cancelled = cancelled with { Occurrence = cancelled.Occurrence with { Event = cancelled.Occurrence.Event with { Status = EventStatus.Cancelled } } };

        Assert.Empty(Plan([AllDay("Birthday", new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 22)), cancelled]));
    }

    [Fact]
    public void Task_with_a_due_time_reminds_at_that_time()
    {
        var reminder = Assert.Single(Plan([TimedTask("Call", Noon)]));

        Assert.Equal(Noon, reminder.FireAt);
        Assert.Equal(ReminderKind.TaskDue, reminder.Kind);
    }

    [Fact]
    public void Task_with_only_a_date_reminds_at_the_configured_time_in_the_display_zone()
    {
        var settings = Defaults with { DateOnlyTaskTime = new TimeOnly(8, 30) };

        var reminder = Assert.Single(Plan([Task("Pay", new DateOnly(2026, 10, 21))], settings, TestData.Warsaw));

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.FromHours(2)), reminder.FireAt);
    }

    [Fact]
    public void Date_only_time_inside_a_spring_forward_gap_moves_to_the_first_valid_time()
    {
        // Europe/Warsaw skips 02:00-03:00 on 2026-03-29.
        var at = ReminderPlanner.AtTimeOfDay(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), TestData.Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), at);
    }

    [Fact]
    public void Date_only_time_that_happens_twice_uses_the_first_one()
    {
        // Europe/Warsaw repeats 02:00-03:00 on 2026-10-25; the first pass is still summer time.
        var at = ReminderPlanner.AtTimeOfDay(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), TestData.Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), at);
    }

    [Fact]
    public void Completed_and_cancelled_tasks_get_no_reminder()
    {
        var done = TimedTask("Done", Noon);
        done = done with { Task = done.Task with { Status = TaskItemStatus.Completed } };
        var cancelled = TimedTask("Cancelled", Noon);
        cancelled = cancelled with { Task = cancelled.Task with { Status = TaskItemStatus.Cancelled } };

        Assert.Empty(Plan([done, cancelled]));
    }

    [Fact]
    public void Tasks_switch_silences_tasks_but_not_events()
    {
        var plan = Plan([TimedTask("Call", Noon), Event("Meet", Noon, Noon.AddHours(1))], Defaults with { Tasks = false });

        Assert.Equal("Meet", Assert.Single(plan).Title);
    }

    [Fact]
    public void Master_switch_off_plans_nothing()
    {
        Assert.Empty(Plan([Event("A", Noon, Noon.AddHours(1)), TimedTask("B", Noon)], Defaults with { Enabled = false }));
    }

    [Fact]
    public void Each_recurring_occurrence_has_its_own_reminder()
    {
        var first = Event("Weekly", Noon, Noon.AddHours(1));
        var second = Event("Weekly", Noon.AddDays(1), Noon.AddDays(1).AddHours(1)) with { };
        second = second with { Occurrence = second.Occurrence with { Event = first.Occurrence.Event } };

        var plan = Plan([first, second]);

        Assert.Equal(2, plan.Count);
        Assert.NotEqual(plan[0].Id, plan[1].Id);
        Assert.Equal(plan[0].Key, plan[1].Key);
    }

    [Fact]
    public void Window_includes_both_edges_and_nothing_outside()
    {
        var entries = new[]
        {
            Event("before", Noon.AddMinutes(-11), Noon.AddHours(1)),
            Event("start", Noon.AddMinutes(-10), Noon.AddHours(1)),
            Event("end", Noon.AddHours(5), Noon.AddHours(6)),
            Event("after", Noon.AddHours(5).AddMinutes(1), Noon.AddHours(6)),
        };

        var plan = Plan(entries, from: Noon.AddMinutes(-20), to: Noon.AddHours(5).AddMinutes(-10));
        // "before" fires at -21 min (outside), "end" fires at +4h50 (the edge), "after" at +4h51 (outside).
        Assert.Equal(["start", "end"], plan.Select(r => r.Title));
    }

    [Fact]
    public void Result_is_ordered_by_fire_time()
    {
        var plan = Plan([Event("late", Noon.AddHours(2), Noon.AddHours(3)), Event("early", Noon, Noon.AddHours(1))]);

        Assert.Equal(["early", "late"], plan.Select(r => r.Title));
    }

    [Fact]
    public async Task Hidden_collections_produce_no_reminder()
    {
        var hidden = new CollectionInfo("s", "c", "Cal", CollectionKind.Calendar, "#112233", IsVisible: false);
        var ev = new CalendarEvent(TestData.Key("e"), "Secret", new TimedEvent(Noon, Noon.AddHours(1)));
        var (_, service) = await ServiceAsync([ev], [], Noon, [hidden]);
        var entries = await service.GetEntriesAsync(Query(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22)), TestContext.Current.CancellationToken);

        Assert.Empty(Plan(entries));
    }

    [Fact]
    public void Text_is_plain_cleaned_and_shortened()
    {
        var title = "Line one\r\n\tline <b>two</b>  " + new string('x', 300);
        var reminder = Assert.Single(Plan([Event(title, Noon, Noon.AddHours(1))]));

        Assert.DoesNotContain('\n', reminder.Title);
        Assert.DoesNotContain("  ", reminder.Title, StringComparison.Ordinal);
        Assert.StartsWith("Line one line <b>two</b> xxx", reminder.Title, StringComparison.Ordinal);
        Assert.True(reminder.Title.Length <= ReminderText.MaxTitleLength + 1);
        Assert.EndsWith("…", reminder.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_title_gets_a_placeholder()
    {
        var entry = Event("x", Noon, Noon.AddHours(1));
        var blank = entry with { Title = "  " };

        Assert.Equal("(no title)", Assert.Single(Plan([blank])).Title);
    }

    [Theory]
    [InlineData("a\u0007b", "a b")]
    [InlineData("  padded  ", "padded")]
    [InlineData(null, "")]
    public void Clean_replaces_control_characters_and_trims(string? input, string expected) =>
        Assert.Equal(expected, ReminderText.Clean(input, 50));

    [Fact]
    public void Clean_does_not_split_a_surrogate_pair()
    {
        var result = ReminderText.Clean("ab😀😀😀", 4);

        Assert.Equal("ab😀…", result);
    }
}
