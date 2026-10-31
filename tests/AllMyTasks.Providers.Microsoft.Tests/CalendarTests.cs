using System.Net;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Microsoft.Tests;

public class CalendarTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    internal const string Delta1 = "https://graph.microsoft.com/v1.0/me/calendars/cal1/calendarView/delta?$deltatoken=d1";
    internal const string Delta2 = "https://graph.microsoft.com/v1.0/me/calendars/cal1/calendarView/delta?$deltatoken=d2";

    private static string Event(string id, string start, string end, string extra = "", string zone = "UTC", string original = "UTC", string subject = "Meeting") =>
        $$"""{"id":"{{id}}","subject":"{{subject}}","start":{"dateTime":"{{start}}","timeZone":"{{zone}}"},"end":{"dateTime":"{{end}}","timeZone":"{{zone}}"},"originalStartTimeZone":"{{original}}"{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static async Task<(CalendarEvent Event, SyncResult<CalendarEvent> Result)> MapOneAsync(string item)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(item, delta: Delta1));
        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        return (Assert.Single(result.Upserts), result);
    }

    [Fact]
    public async Task Collections_are_read_only_calendars_with_valid_colours_only()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Page("""{"id":"a","name":"Calendar","hexColor":"#FF8800"},{"id":"b","name":"Holidays","hexColor":"auto"},{"name":"No id"}"""),
            todo: false);

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["Calendar", "Holidays"], collections.Select(c => c.DisplayName));
        Assert.Equal(["#ff8800", null], collections.Select(c => c.Color));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.Kind == CollectionKind.Calendar));
    }

    [Fact]
    public async Task A_timed_event_is_read_as_utc_with_its_authoring_zone()
    {
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:30:00.0000000", original: "W. Europe Standard Time"));

        var time = Assert.IsType<TimedEvent>(mapped.Time);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), time.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), time.End);
        Assert.Equal("Europe/Berlin", time.TimeZoneId); // the canonical IANA id of the Windows zone
        Assert.Null(mapped.Recurrence);
        Assert.Equal(new ItemKey(Setup.SourceId, "cal1", "e1"), mapped.Key);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task An_all_day_event_keeps_its_dates_in_its_own_zone()
    {
        // Midnight Warsaw on 3 and 4 November is 23:00 UTC the evening before (CET).
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-11-02T23:00:00.0000000", "2026-11-04T23:00:00.0000000", "\"isAllDay\":true", original: "W. Europe Standard Time"));

        var time = Assert.IsType<AllDayEvent>(mapped.Time);
        Assert.Equal(new DateOnly(2026, 11, 3), time.Start);
        Assert.Equal(new DateOnly(2026, 11, 5), time.EndExclusive);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task An_all_day_event_across_a_dst_change_still_lands_on_whole_days()
    {
        // Warsaw leaves summer time on 2026-10-25: midnight 24 Oct is 22:00 UTC (CEST), midnight 26 Oct is 23:00 UTC (CET).
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-10-23T22:00:00.0000000", "2026-10-25T23:00:00.0000000", "\"isAllDay\":true", original: "Europe/Warsaw"));

        var time = Assert.IsType<AllDayEvent>(mapped.Time);
        Assert.Equal(new DateOnly(2026, 10, 24), time.Start);
        Assert.Equal(new DateOnly(2026, 10, 26), time.EndExclusive);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task An_all_day_event_that_is_not_at_midnight_uses_the_nearest_day_and_warns()
    {
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-11-03T22:00:00.0000000", "2026-11-04T22:00:00.0000000", "\"isAllDay\":true", original: "UTC"));

        var time = Assert.IsType<AllDayEvent>(mapped.Time);
        Assert.Equal(new DateOnly(2026, 11, 4), time.Start);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Times_in_another_zone_are_converted_to_instants()
    {
        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T09:00:00.0000000", "2026-11-02T10:00:00.0000000", zone: "W. Europe Standard Time"));

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), Assert.IsType<TimedEvent>(mapped.Time).Start);
    }

    [Fact]
    public async Task An_unknown_original_zone_keeps_the_event_and_warns()
    {
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", original: "Customized Time Zone"));

        Assert.Null(Assert.IsType<TimedEvent>(mapped.Time).TimeZoneId);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task An_event_that_ends_before_it_starts_becomes_a_point_and_warns()
    {
        var (mapped, result) = await MapOneAsync(Event("e1", "2026-11-02T09:00:00.0000000", "2026-11-02T08:00:00.0000000"));

        var time = Assert.IsType<TimedEvent>(mapped.Time);
        Assert.Equal(time.Start, time.End);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("\"isCancelled\":true", EventStatus.Cancelled)]
    [InlineData("\"responseStatus\":{\"response\":\"declined\"}", EventStatus.Cancelled)]
    [InlineData("\"showAs\":\"tentative\"", EventStatus.Tentative)]
    [InlineData("\"responseStatus\":{\"response\":\"tentativelyAccepted\"}", EventStatus.Tentative)]
    [InlineData("\"showAs\":\"busy\",\"responseStatus\":{\"response\":\"accepted\"}", EventStatus.Confirmed)]
    public async Task Status_follows_cancellation_and_the_response(string extra, EventStatus expected)
    {
        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.Equal(expected, mapped.Status);
    }

    [Fact]
    public async Task Free_time_location_link_notes_and_categories_are_mapped()
    {
        var extra = """
            "showAs":"free","body":{"contentType":"text","content":"Agenda"},"location":{"displayName":""},
            "locations":[{"displayName":"Room 1"},{"displayName":"Room 2"}],
            "onlineMeeting":{"joinUrl":"javascript:alert(1)"},"webLink":"https://outlook.live.com/owa/?itemid=1",
            "categories":["Red","Work"],"lastModifiedDateTime":"2026-10-20T10:00:00.0000000Z"
            """;

        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.Equal(Availability.Free, mapped.Availability);
        Assert.Equal("Agenda", mapped.Description);
        Assert.Equal("Room 1; Room 2", mapped.Location);
        Assert.Equal("https://outlook.live.com/owa/?itemid=1", mapped.Url?.AbsoluteUri);
        Assert.Equal(["Red", "Work"], mapped.Categories);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), mapped.LastModified);
    }

    [Fact]
    public async Task A_teams_join_url_becomes_the_meeting_link_and_the_web_link_stays_the_url()
    {
        var extra = "\"onlineMeeting\":{\"joinUrl\":\"https://teams.microsoft.com/l/meetup-join/1\"},\"webLink\":\"https://outlook.live.com/owa/?itemid=1\"";

        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.Equal("https://teams.microsoft.com/l/meetup-join/1", mapped.MeetingUrl?.AbsoluteUri);
        Assert.Equal("https://outlook.live.com/owa/?itemid=1", mapped.Url?.AbsoluteUri);
    }

    [Theory]
    [InlineData("\"onlineMeeting\":{\"joinUrl\":\"javascript:alert(1)\"}", null)]
    [InlineData("\"onlineMeeting\":{\"joinUrl\":\"https://user:pw@teams.microsoft.com/x\"}", null)]
    [InlineData("\"onlineMeeting\":null,\"onlineMeetingUrl\":\"https://teams.microsoft.com/l/old\"", "https://teams.microsoft.com/l/old")]
    [InlineData("\"isOnlineMeeting\":false", null)]
    public async Task The_meeting_link_is_checked_and_falls_back_to_the_older_property(string extra, string? expected)
    {
        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.Equal(expected, mapped.MeetingUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task Organiser_and_attendees_are_mapped_with_roles_and_answers()
    {
        var extra = """
            "isOrganizer":false,
            "organizer":{"emailAddress":{"name":"Olga","address":"o@example.com"}},
            "attendees":[
              {"type":"required","status":{"response":"accepted"},"emailAddress":{"name":"Ann","address":"ann@example.com"}},
              {"type":"optional","status":{"response":"tentativelyAccepted"},"emailAddress":{"address":"bob@example.com"}},
              {"type":"resource","status":{"response":"none"},"emailAddress":{"name":"Room 4"}},
              {"type":"required","status":{"response":"declined"},"emailAddress":{"name":"Cy"}},
              {"type":"required","status":{"response":"notResponded"},"emailAddress":{}}
            ]
            """;

        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.Equal(new Attendee("Olga", "o@example.com", AttendeeRole.Required, ResponseStatus.Accepted, false), mapped.Organizer);
        Assert.Equal(
            [
                new Attendee("Ann", "ann@example.com", AttendeeRole.Required, ResponseStatus.Accepted),
                new Attendee(null, "bob@example.com", AttendeeRole.Optional, ResponseStatus.Tentative),
                new Attendee("Room 4", null, AttendeeRole.Resource, ResponseStatus.NeedsAction),
                new Attendee("Cy", null, AttendeeRole.Required, ResponseStatus.Declined),
            ],
            mapped.Attendees);
        Assert.Equal(4, mapped.AttendeeCount);
    }

    [Fact]
    public async Task The_organiser_flag_marks_the_account_owner_and_a_huge_list_is_capped_but_counted()
    {
        var people = string.Join(",", Enumerable.Range(0, 250).Select(i => $$$"""{"type":"required","emailAddress":{"name":"P{{{i}}}","address":"p{{{i}}}@example.com"}}"""));
        var extra = $$$"""
            "isOrganizer":true,"organizer":{"emailAddress":{"name":"Me","address":"me@example.com"}},"attendees":[{{{people}}}]
            """;

        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000", extra));

        Assert.True(mapped.Organizer!.IsSelf);
        Assert.Equal(Attendee.MaxPerEvent, mapped.Attendees.Count);
        Assert.Equal(250, mapped.AttendeeCount);
    }

    [Fact]
    public async Task An_event_without_meeting_data_has_none()
    {
        var (mapped, _) = await MapOneAsync(Event("e1", "2026-11-02T08:00:00.0000000", "2026-11-02T09:00:00.0000000"));

        Assert.Null(mapped.MeetingUrl);
        Assert.Null(mapped.Organizer);
        Assert.Empty(mapped.Attendees);
    }

    [Fact]
    public async Task An_event_with_no_subject_keeps_an_empty_title_and_a_bad_event_is_skipped_with_a_warning()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(
            """{"id":"ok","start":{"dateTime":"2026-11-02T08:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-02T09:00:00","timeZone":"UTC"}},{"id":"bad","start":{"dateTime":"nonsense","timeZone":"UTC"},"end":null}""",
            delta: Delta1));

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.Equal(string.Empty, Assert.Single(result.Upserts).Title);
        Assert.Equal("bad", Assert.Single(result.Warnings).ItemId);
    }

    private const string NoSubject = """{"id":"h1","start":{"dateTime":"2026-11-01T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-02T00:00:00","timeZone":"UTC"},"isAllDay":true}""";

    [Fact]
    public async Task An_event_the_delta_query_returned_without_a_subject_gets_it_from_a_single_read()
    {
        var (handler, source, _, _) = Setup.Create(request =>
            request.RequestUri!.AbsolutePath.Contains("/me/events/", StringComparison.Ordinal)
                ? FakeHandler.Json("""{"id":"h1","subject":"Wszystkich Świętych"}""")
                : Setup.Page(NoSubject, delta: Delta1));

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.Equal("Wszystkich Świętych", Assert.Single(result.Upserts).Title);
        Assert.Empty(result.Warnings);
        Assert.Contains(handler.GraphRequests, r => r.RequestUri!.AbsoluteUri.EndsWith("/me/events/h1?$select=subject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_subject_read_keeps_the_event_with_an_empty_title()
    {
        var (_, source, _, _) = Setup.Create(request =>
            request.RequestUri!.AbsolutePath.Contains("/me/events/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Setup.Page(NoSubject, delta: Delta1));

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.Equal(string.Empty, Assert.Single(result.Upserts).Title);
    }

    [Fact]
    public async Task The_first_sync_reads_the_window_across_pages_and_is_a_snapshot()
    {
        var calls = 0;
        var (handler, source, _, _) = Setup.Create(request =>
        {
            calls++;
            return request.RequestUri!.Query.Contains("skip", StringComparison.Ordinal)
                ? Setup.Page(Event("e2", "2026-11-03T08:00:00", "2026-11-03T09:00:00"), delta: Delta1)
                : Setup.Page(Event("e1", "2026-11-02T08:00:00", "2026-11-02T09:00:00"), next: "https://graph.microsoft.com/v1.0/me/calendars/cal1/calendarView/delta?$skiptoken=s1");
        });

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["e1", "e2"], result.Upserts.Select(e => e.Key.ItemId));
        Assert.Equal(2, calls);
        var first = handler.GraphRequests.First();
        Assert.Contains("startDateTime=2026-07-01T00:00:00Z", first.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("endDateTime=2027-11-01T00:00:00Z", first.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("outlook.timezone=\"UTC\"", string.Join(',', first.Headers.GetValues("Prefer")), StringComparison.Ordinal);
        Assert.Equal("Bearer", first.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task A_later_sync_uses_the_saved_delta_link_and_reports_changes_and_removals()
    {
        var phase = 0;
        var (handler, source, _, time) = Setup.Create(request => phase == 0
            ? Setup.Page(Event("e1", "2026-11-02T08:00:00", "2026-11-02T09:00:00"), delta: Delta1)
            : Setup.Page(
                """{"id":"e1","@removed":{"reason":"deleted"}},""" + Event("e3", "2026-11-04T08:00:00", "2026-11-04T09:00:00"),
                delta: Delta2));
        var first = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        phase = 1;
        time.Advance(TimeSpan.FromHours(1));

        var second = await source.SyncEventsAsync(Setup.Calendar(), first.NewState, Ct);

        Assert.False(second.IsFullSnapshot);
        Assert.Equal(["e1"], second.DeletedItemIds);
        Assert.Equal(["e3"], second.Upserts.Select(e => e.Key.ItemId));
        Assert.Equal(Delta1, handler.GraphRequests.Last().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task An_empty_delta_is_normal_and_moves_the_position()
    {
        var phase = 0;
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(string.Empty, delta: phase == 0 ? Delta1 : Delta2));
        var first = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        phase = 1;

        var second = await source.SyncEventsAsync(Setup.Calendar(), first.NewState, Ct);

        Assert.Empty(second.Upserts);
        Assert.NotEqual(first.NewState.Token, second.NewState.Token);
    }

    [Fact]
    public async Task The_window_rolls_into_a_new_snapshot_when_under_nine_months_remain()
    {
        var (handler, source, _, time) = Setup.Create(_ => Setup.Page(Event("e1", "2026-11-02T08:00:00", "2026-11-02T09:00:00"), delta: Delta1));
        var first = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        time.Advance(TimeSpan.FromDays(130));

        var second = await source.SyncEventsAsync(Setup.Calendar(), first.NewState, Ct);

        Assert.True(second.IsFullSnapshot);
        Assert.Contains("startDateTime=2026-12-01T00:00:00Z", handler.GraphRequests.Last().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_with_neither_link_is_an_error_not_an_empty_result()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(Event("e1", "2026-11-02T08:00:00", "2026-11-02T09:00:00")));

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncEventsAsync(Setup.Calendar(), null, Ct));
    }

    [Fact]
    public async Task An_expired_delta_link_throws_sync_state_expired()
    {
        var phase = 0;
        var (_, source, _, _) = Setup.Create(_ => phase == 0
            ? Setup.Page(string.Empty, delta: Delta1)
            : FakeHandler.Json("""{"error":{"code":"syncStateNotFound"}}""", HttpStatusCode.Gone));
        var first = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        phase = 1;

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncEventsAsync(Setup.Calendar(), first.NewState, Ct));
    }

    [Fact]
    public async Task A_saved_position_that_points_off_host_is_treated_as_expired_and_nothing_is_sent()
    {
        var (handler, source, _, _) = Setup.Create(_ => Setup.Page(string.Empty, delta: Delta1));
        var state = new SyncState("""{"v":1,"deltaLink":"https://evil.example/steal","start":"2026-07-01T00:00:00+00:00","end":"2027-11-01T00:00:00+00:00"}""");

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncEventsAsync(Setup.Calendar(), state, Ct));

        Assert.Empty(handler.GraphRequests);
    }

    [Fact]
    public async Task A_next_link_on_another_host_is_refused_and_the_token_is_not_sent_there()
    {
        var (handler, source, _, _) = Setup.Create(_ => Setup.Page(string.Empty, next: "https://evil.example/page2"));

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncEventsAsync(Setup.Calendar(), null, Ct));

        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public async Task A_calendar_that_is_gone_keeps_the_cache_and_warns()
    {
        var (_, source, _, _) = Setup.Create(_ => FakeHandler.Json("""{"error":{"code":"ErrorItemNotFound"}}""", HttpStatusCode.NotFound));
        var previous = new SyncState("""{"v":1}""");

        var result = await source.SyncEventsAsync(Setup.Calendar(), previous, Ct);

        Assert.False(result.IsFullSnapshot);
        Assert.Empty(result.Upserts);
        Assert.Single(result.Warnings);
        Assert.Equal(previous, result.NewState);
    }
}
