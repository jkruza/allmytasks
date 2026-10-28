using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class AttendeeTests
{
    private static Attendee Person(int i, AttendeeRole role = AttendeeRole.Required, ResponseStatus response = ResponseStatus.NeedsAction) =>
        new($"Person {i}", $"p{i}@example.com", role, response);

    private static EntryDetails Build(CalendarEvent ev)
    {
        var template = ViewTestKit.Event("x", ViewTestKit.Utc_(2026, 10, 26, 10), ViewTestKit.Utc_(2026, 10, 26, 11));
        var entry = template with { Occurrence = new EventOccurrence(ev, template.Occurrence.Time) };
        return EntryDetailsBuilder.Create(entry, TimeZoneInfo.Utc, null, CultureInfo.InvariantCulture);
    }

    private static CalendarEvent Event(Attendee? organizer = null, IReadOnlyList<Attendee>? attendees = null, Uri? meeting = null, int count = 0) =>
        new(TestData.Key(), "x", new TimedEvent(ViewTestKit.Utc_(2026, 10, 26, 10), ViewTestKit.Utc_(2026, 10, 26, 11)),
            MeetingUrl: meeting, Organizer: organizer, Attendees: attendees, AttendeeCount: count);

    [Fact]
    public void Cap_keeps_200_and_reports_the_real_count()
    {
        var list = Attendee.Cap(Enumerable.Range(0, 340).Select(i => Person(i)), out var total);

        Assert.Equal(Attendee.MaxPerEvent, list.Count);
        Assert.Equal(340, total);
        Assert.Equal("Person 0", list[0].Name);
    }

    [Fact]
    public void Cap_drops_entries_without_name_or_email_and_cuts_overlong_text()
    {
        var list = Attendee.Cap([new Attendee(null, " "), new Attendee(new string('n', 1000), "a@example.com")], out var total);

        Assert.Equal(1, total);
        Assert.Equal(Attendee.MaxTextLength, Assert.Single(list).Name!.Length);
    }

    [Fact]
    public void Display_text_falls_back_to_the_email() => Assert.Equal("a@example.com", new Attendee(" ", "a@example.com").DisplayText);

    [Fact]
    public void A_task_or_an_event_without_people_has_no_meeting_or_attendee_data()
    {
        var details = Build(Event());

        Assert.Null(details.MeetingUrl);
        Assert.Null(details.MeetingLabel);
        Assert.Empty(details.People);
        Assert.Equal(0, details.MoreAttendees);
    }

    [Fact]
    public void The_join_button_names_the_service_and_the_host()
    {
        var details = Build(Event(meeting: new Uri("https://meet.google.com/abc-defg-hij")));

        Assert.Equal("Join Google Meet", details.MeetingLabel);
        Assert.Equal("meet.google.com", details.MeetingHost);
        Assert.Equal("https://meet.google.com/abc-defg-hij", details.MeetingUrl?.AbsoluteUri);
    }

    [Fact]
    public void People_are_ordered_organiser_required_optional_then_resources_and_the_organiser_is_not_repeated()
    {
        var organizer = new Attendee("Olga", "o@example.com");
        var details = Build(Event(
            organizer,
            [
                Person(1, AttendeeRole.Optional),
                new Attendee("Room 4", "room@example.com", AttendeeRole.Resource, ResponseStatus.Accepted),
                new Attendee("Olga", "O@example.com", AttendeeRole.Required, ResponseStatus.Accepted),
                Person(2),
            ]));

        Assert.Equal(["Olga", "Person 2", "Person 1", "Room 4"], details.People.Select(p => p.Text));
        Assert.Equal(["Organiser", null, "Optional", "Room"], details.People.Select(p => p.Note));
        Assert.Equal("✓", details.People[0].Glyph);
    }

    [Fact]
    public void Response_glyphs_and_words_cover_every_status()
    {
        var details = Build(Event(attendees:
        [
            Person(1, response: ResponseStatus.Accepted),
            Person(2, response: ResponseStatus.Tentative),
            Person(3, response: ResponseStatus.Declined),
            Person(4),
        ]));

        Assert.Equal(["✓", "?", "✗", "…"], details.People.Select(p => p.Glyph));
        Assert.Equal("Person 3, declined", details.People[2].Spoken);
    }

    [Fact]
    public void The_email_is_the_tooltip_and_the_fallback_text_and_self_is_you()
    {
        var details = Build(Event(attendees:
        [
            new Attendee("Ann", "ann@example.com"),
            new Attendee(null, "bob@example.com"),
            new Attendee("Me", "me@example.com", IsSelf: true),
        ]));

        Assert.Equal(("Ann", "ann@example.com"), (details.People[0].Text, details.People[0].Tooltip));
        Assert.Equal(("bob@example.com", null), (details.People[1].Text, details.People[1].Tooltip));
        Assert.Equal(("You", "Me, me@example.com"), (details.People[2].Text, details.People[2].Tooltip));
    }

    [Fact]
    public void The_list_is_collapsed_after_five_and_expands_on_request()
    {
        var details = Build(Event(attendees: [.. Enumerable.Range(1, 8).Select(i => Person(i))]));

        Assert.Equal(8, details.People.Count);
        Assert.Equal(EntryDetails.CollapsedCount, details.VisiblePeople(false).Count);
        Assert.Equal(8, details.VisiblePeople(true).Count);
        Assert.Equal(5, Build(Event(attendees: [.. Enumerable.Range(1, 5).Select(i => Person(i))])).VisiblePeople(false).Count);
    }

    [Fact]
    public void Attendees_over_the_cap_are_counted_as_more()
    {
        var capped = Attendee.Cap(Enumerable.Range(0, 340).Select(i => Person(i)), out var total);

        Assert.Equal(140, Build(Event(attendees: capped, count: total)).MoreAttendees);
    }
}
