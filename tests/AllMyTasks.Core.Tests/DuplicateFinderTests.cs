using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;
using static AllMyTasks.Core.Tests.ViewTestKit;

namespace AllMyTasks.Core.Tests;

public sealed class DuplicateFinderTests
{
    private static readonly DateTimeOffset Nine = Utc_(2026, 11, 11, 9);

    private static EventEntry Ev(string source, string title, DateTimeOffset start, int minutes = 60, string? uid = null, string? location = null, string? meeting = null, string? organizer = null, string? collection = null, EventStatus status = EventStatus.Confirmed)
    {
        var end = start.AddMinutes(minutes);
        var time = new TimedEvent(start, end);
        var ev = new CalendarEvent(
            new ItemKey(source, "c", $"{title}-{source}"),
            title,
            time,
            Location: location,
            Status: status,
            MeetingUrl: meeting is null ? null : new Uri(meeting),
            Organizer: organizer is null ? null : new Attendee(null, organizer, AttendeeRole.Required, ResponseStatus.Accepted),
            Uid: uid);
        return new EventEntry(new EventOccurrence(ev, time), null, collection ?? source, start, end, false, DateOnly.FromDateTime(start.UtcDateTime), DateOnly.FromDateTime(start.UtcDateTime).AddDays(1));
    }

    private static TaskEntry Tk(string source, string title, DateOnly day, string? url)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var item = new TaskItem(new ItemKey(source, "l", $"{title}-{source}"), title, Due: new DueDate(day), Url: url is null ? null : new Uri(url));
        return new TaskEntry(item, null, source, start, start.AddDays(1), true, day, day.AddDays(1), false);
    }

    private static readonly DuplicateOptions EventsOnly = new(true, false);

    [Fact]
    public void Same_uid_and_time_is_one_event_even_when_the_titles_differ()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Planning", Nine, uid: "U1"), Ev("b", "Planning (copy)", Nine, uid: "u1")], EventsOnly, ["a", "b"]);

        var only = Assert.Single(result);
        Assert.Equal("a", only.Key.SourceId);
        Assert.Equal(["b"], only.AlsoIn);
    }

    [Fact]
    public void Same_uid_at_another_time_is_a_different_occurrence()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Standup", Nine, uid: "U1"), Ev("b", "Standup", Nine.AddDays(1), uid: "U1")], EventsOnly, ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Same_title_and_time_alone_is_not_enough()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Standup", Nine), Ev("b", "Standup", Nine)], EventsOnly, ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("https://meet.example/x", null, null)]
    [InlineData(null, "boss@example.com", null)]
    [InlineData(null, null, "Room 4")]
    public void Same_title_and_time_with_one_more_sign_is_merged(string? meeting, string? organizer, string? location)
    {
        var result = DuplicateFinder.Collapse(
            [Ev("a", "Standup", Nine, meeting: meeting, organizer: organizer, location: location), Ev("b", "  STANDUP ", Nine, meeting: meeting, organizer: organizer?.ToUpperInvariant(), location: location?.ToLowerInvariant())],
            EventsOnly,
            ["a", "b"]);

        Assert.Single(result);
    }

    [Fact]
    public void Titles_ignore_case_accents_and_extra_spaces()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Café  sync", Nine, location: "R1"), Ev("b", "cafe sync", Nine, location: "R1")], EventsOnly, ["a", "b"]);

        Assert.Single(result);
    }

    [Fact]
    public void Same_title_with_another_time_is_not_merged()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, location: "R1"), Ev("b", "Sync", Nine.AddMinutes(30), location: "R1")], EventsOnly, ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Same_time_with_another_title_is_not_merged()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, location: "R1"), Ev("b", "Review", Nine, location: "R1")], EventsOnly, ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Two_events_of_the_same_source_are_never_merged()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, uid: "U1"), Ev("a", "Sync 2", Nine, uid: "U1")], EventsOnly, ["a"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void A_cancelled_copy_is_not_merged_with_a_live_one()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, uid: "U1"), Ev("b", "Sync", Nine, uid: "U1", status: EventStatus.Cancelled)], EventsOnly, ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Three_way_group_keeps_one_and_lists_the_others()
    {
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, uid: "U", collection: "Work"), Ev("b", "Sync", Nine, uid: "U", collection: "Personal"), Ev("c", "Sync", Nine, uid: "U", collection: "Shared")], EventsOnly, ["a", "b", "c"]);

        var only = Assert.Single(result);
        Assert.Equal(["Personal", "Shared"], only.AlsoIn);
    }

    [Fact]
    public void The_source_with_the_best_priority_survives()
    {
        var entries = new[] { Ev("a", "Sync", Nine, uid: "U"), Ev("b", "Sync", Nine, uid: "U") };

        Assert.Equal("a", Assert.Single(DuplicateFinder.Collapse(entries, EventsOnly, ["a", "b"])).Key.SourceId);
        Assert.Equal("b", Assert.Single(DuplicateFinder.Collapse(entries, new DuplicateOptions(true, false, ["b", "a"]), ["a", "b"])).Key.SourceId);
    }

    [Fact]
    public void Surviving_entries_keep_their_order()
    {
        var first = Ev("a", "First", Nine.AddHours(-1));
        var result = DuplicateFinder.Collapse([first, Ev("a", "Sync", Nine, uid: "U"), Ev("b", "Sync", Nine, uid: "U"), Ev("a", "Last", Nine.AddHours(3))], EventsOnly, ["a", "b"]);

        Assert.Equal(["First", "Sync", "Last"], result.Select(e => e.Title));
    }

    [Fact]
    public void Events_off_leaves_everything()
    {
        var entries = new[] { Ev("a", "Sync", Nine, uid: "U"), Ev("b", "Sync", Nine, uid: "U") };

        Assert.Same(entries, DuplicateFinder.Collapse(entries, DuplicateOptions.Off, ["a", "b"]));
        Assert.Equal(2, DuplicateFinder.Collapse(entries, new DuplicateOptions(false, true), ["a", "b"]).Count);
    }

    [Fact]
    public void Tasks_need_the_option_a_title_a_date_and_a_link()
    {
        var day = new DateOnly(2026, 11, 12);
        var pair = new[] { Tk("a", "Pay rent", day, "https://x.example/1"), Tk("b", "pay rent", day, "https://x.example/1") };

        Assert.Equal(2, DuplicateFinder.Collapse(pair, EventsOnly, ["a", "b"]).Count);
        Assert.Single(DuplicateFinder.Collapse(pair, new DuplicateOptions(false, true), ["a", "b"]));
        Assert.Equal(2, DuplicateFinder.Collapse([Tk("a", "Pay rent", day, null), Tk("b", "Pay rent", day, null)], new DuplicateOptions(false, true), ["a", "b"]).Count);
        Assert.Equal(2, DuplicateFinder.Collapse([Tk("a", "Pay rent", day, "https://x.example/1"), Tk("b", "Pay rent", day.AddDays(1), "https://x.example/1")], new DuplicateOptions(false, true), ["a", "b"]).Count);
    }

    [Fact]
    public void An_event_and_a_task_are_never_merged()
    {
        var at = new DateOnly(2026, 11, 11);
        var task = Tk("b", "Sync", at, "https://x.example/1");
        var result = DuplicateFinder.Collapse([Ev("a", "Sync", task.Start, minutes: 1440, uid: "U"), task], new DuplicateOptions(true, true), ["a", "b"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task The_agenda_service_collapses_when_asked_and_keeps_both_in_the_store()
    {
        var uidA = new CalendarEvent(new ItemKey("s", "c", "1"), "Sync", new TimedEvent(Nine, Nine.AddHours(1)), Uid: "U");
        var uidB = new CalendarEvent(new ItemKey("s", "c", "2"), "Sync", new TimedEvent(Nine, Nine.AddHours(1)), Uid: "U");
        var (store, _) = await ServiceAsync([uidA], [], Nine);
        await store.ApplyAsync("t", "c", AllMyTasks.Core.Providers.SyncResult.Snapshot<CalendarEvent>([uidB with { Key = new ItemKey("t", "c", "2") }], new AllMyTasks.Core.Providers.SyncState("x")), TestContext.Current.CancellationToken);
        var query = Query(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12));

        var on = new AgendaService(store, store, new FakeRecurrenceExpander(), new FixedClock(Nine), () => EventsOnly);
        var off = new AgendaService(store, store, new FakeRecurrenceExpander(), new FixedClock(Nine), () => DuplicateOptions.Off);

        Assert.Single(await on.GetEntriesAsync(query, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await off.GetEntriesAsync(query, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public void Details_name_where_else_the_item_appears()
    {
        var entry = DuplicateFinder.Collapse([Ev("a", "Sync", Nine, uid: "U", collection: "Work"), Ev("b", "Sync", Nine, uid: "U", collection: "Personal")], EventsOnly, ["a", "b"])[0];

        var details = EntryDetailsBuilder.Create(entry, Utc, null);

        Assert.Contains(details.Rows, r => r.Label == "Also in" && r.Value == "Personal");
    }

    [Fact]
    public void Settings_default_to_events_on_and_tasks_off_and_round_trip()
    {
        Assert.True(new AppSettings().HideDuplicateEvents);
        Assert.False(new AppSettings().HideDuplicateTasks);

        var changed = AppSettings.Parse((new AppSettings() with { HideDuplicateEvents = false, HideDuplicateTasks = true }).ToJson());

        Assert.False(changed.HideDuplicateEvents);
        Assert.True(changed.HideDuplicateTasks);
        Assert.Equal(new DuplicateOptions(false, true), changed.ToDuplicateOptions());
    }
}
