using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class EntryDetailsBuilderTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static EntryDetails Build(AgendaEntry entry, TimeZoneInfo? zone = null, string? source = null) =>
        EntryDetailsBuilder.Create(entry, zone ?? TimeZoneInfo.Utc, source, Culture);

    private static string? Row(EntryDetails d, string label) => d.Rows.FirstOrDefault(r => r.Label == label)?.Value;

    private static EventEntry WithEvent(CalendarEvent ev, EventEntry template) =>
        template with { Occurrence = new EventOccurrence(ev, template.Occurrence.Time) };

    private static EventEntry Timed(string title = "Sync") =>
        ViewTestKit.Event(title, ViewTestKit.Utc_(2026, 10, 26, 10), ViewTestKit.Utc_(2026, 10, 26, 11));

    [Fact]
    public void Timed_single_day_event_shows_date_and_range()
    {
        Assert.Equal("Mon 26 Oct 2026, 10:00 – 11:00", Row(Build(Timed()), "When"));
    }

    [Fact]
    public void Time_is_shown_in_the_display_zone()
    {
        Assert.Equal("Mon 26 Oct 2026, 11:00 – 12:00", Row(Build(Timed(), TestData.Warsaw), "When"));
    }

    [Fact]
    public void Multi_day_timed_event_shows_both_dates()
    {
        var entry = ViewTestKit.Event("Trip", ViewTestKit.Utc_(2026, 10, 26, 22), ViewTestKit.Utc_(2026, 10, 27, 9));
        Assert.Equal("Mon 26 Oct 2026, 22:00 – Tue 27 Oct 2026, 09:00", Row(Build(entry), "When"));
    }

    [Fact]
    public void Point_in_time_shows_only_the_start()
    {
        var at = ViewTestKit.Utc_(2026, 10, 26, 10);
        Assert.Equal("Mon 26 Oct 2026, 10:00", Row(Build(ViewTestKit.Event("Ping", at, at)), "When"));
    }

    [Fact]
    public void All_day_event_says_so()
    {
        var entry = ViewTestKit.AllDay("Holiday", new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27));
        Assert.Equal("Mon 26 Oct 2026, all day", Row(Build(entry), "When"));
    }

    [Fact]
    public void Multi_day_all_day_event_shows_range_and_count()
    {
        var entry = ViewTestKit.AllDay("Conference", new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 29));
        Assert.Equal("Mon 26 Oct 2026 – Wed 28 Oct 2026 (3 days)", Row(Build(entry), "When"));
    }

    [Fact]
    public void Series_occurrence_shows_its_own_time_and_a_repeat_summary()
    {
        var template = Timed();
        var ev = template.Occurrence.Event with { Recurrence = new Recurrence("FREQ=WEEKLY;BYDAY=MO") };
        var details = Build(WithEvent(ev, template));
        Assert.Equal("Mon 26 Oct 2026, 10:00 – 11:00", Row(details, "When"));
        Assert.Equal("Every week on Monday", Row(details, "Repeats"));
    }

    [Fact]
    public void Changed_occurrence_is_labelled()
    {
        var template = Timed();
        var key = template.Occurrence.Event.Key with { RecurrenceId = ViewTestKit.Utc_(2026, 10, 26, 10) };
        var details = Build(WithEvent(template.Occurrence.Event with { Key = key }, template));
        Assert.Contains("changed", Row(details, "Repeats"), StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_fields_without_values_produce_no_rows()
    {
        var details = Build(Timed());
        Assert.Equal(["When", "Calendar"], details.Rows.Select(r => r.Label));
        Assert.Null(details.Description);
        Assert.Null(details.Link);
    }

    [Fact]
    public void All_event_fields_are_shown()
    {
        var template = Timed();
        var ev = template.Occurrence.Event with
        {
            Location = "Room 4",
            Description = "Agenda attached",
            Url = new Uri("https://example.com/meet"),
            Status = EventStatus.Tentative,
            Availability = Availability.Free,
            Categories = ["Work", "Team"],
            LastModified = ViewTestKit.Utc_(2026, 10, 20, 8),
        };
        var details = Build(WithEvent(ev, template), source: "Fastmail");

        Assert.Equal("Room 4", Row(details, "Where"));
        Assert.Equal("Cal, from Fastmail", Row(details, "Calendar"));
        Assert.Equal("Tentative", Row(details, "Status"));
        Assert.Equal("Free", Row(details, "Show as"));
        Assert.Equal("Work, Team", Row(details, "Categories"));
        Assert.Equal("https://example.com/meet", Row(details, "Link"));
        Assert.Equal("Tue 20 Oct 2026, 08:00", Row(details, "Last changed"));
        Assert.Equal("Agenda attached", details.Description);
        Assert.Equal(new Uri("https://example.com/meet"), details.Link);
    }

    [Fact]
    public void Source_with_the_same_name_as_the_calendar_is_not_repeated()
    {
        Assert.Equal("Cal", Row(Build(Timed(), source: "Cal"), "Calendar"));
    }

    [Theory]
    [InlineData("mailto:a@example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Links_with_other_schemes_are_shown_but_not_openable(string url)
    {
        var template = Timed();
        var details = Build(WithEvent(template.Occurrence.Event with { Url = new Uri(url) }, template));
        Assert.Null(details.Link);
        Assert.NotNull(Row(details, "Link"));
    }

    [Fact]
    public void Empty_title_gets_a_placeholder()
    {
        Assert.Equal("(no title)", Build(Timed() with { Title = " " }).Title);
    }

    [Fact]
    public void Very_long_description_is_capped()
    {
        var template = Timed();
        var details = Build(WithEvent(template.Occurrence.Event with { Description = new string('x', EntryDetailsBuilder.MaxDescriptionLength + 500) }, template));
        Assert.Equal(EntryDetailsBuilder.MaxDescriptionLength + 1, details.Description!.Length);
        Assert.EndsWith("…", details.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_colour_falls_back_to_the_default()
    {
        var entry = ViewTestKit.AllDay("Holiday", new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27));
        Assert.Equal(ColorUtil.DefaultColor, Build(entry).AccentColor);
    }

    [Fact]
    public void Task_shows_due_list_status_priority_labels_and_notes()
    {
        var entry = ViewTestKit.Task("Pay rent", new DateOnly(2026, 10, 30));
        var task = entry.Task with
        {
            Notes = "Transfer first",
            Status = TaskItemStatus.Completed,
            CompletedAt = ViewTestKit.Utc_(2026, 10, 28, 9),
            Priority = Priority.High,
            Labels = ["home"],
        };
        var details = Build(entry with { Task = task }, source: "Todoist");

        Assert.True(details.IsTask);
        Assert.Equal("Fri 30 Oct 2026", Row(details, "Due"));
        Assert.Equal("List, from Todoist", Row(details, "List"));
        Assert.Equal("Completed on 28 Oct 2026", Row(details, "Status"));
        Assert.Equal("High", Row(details, "Priority"));
        Assert.Equal("home", Row(details, "Labels"));
        Assert.Equal("Transfer first", details.Description);
    }

    [Fact]
    public void Timed_task_shows_its_due_time()
    {
        var entry = ViewTestKit.TimedTask("Call", ViewTestKit.Utc_(2026, 10, 26, 15, 30));
        Assert.Equal("Mon 26 Oct 2026, 15:30", Row(Build(entry), "Due"));
    }
}
