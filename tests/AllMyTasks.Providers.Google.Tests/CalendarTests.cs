using System.Net;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Google.Tests;

public class CalendarTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<(CalendarEvent? Event, SyncResult<CalendarEvent> Result)> MapOneAsync(string json)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Events(json, sync: "s1"), tasks: false);
        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);
        return ((result.Upserts.Count > 0 ? result.Upserts[0] : null), result);
    }

    [Fact]
    public async Task Calendars_map_name_override_colour_and_visibility_and_are_read_only()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Page("""
                {"id":"a@example.com","summary":"Work","summaryOverride":"My work","backgroundColor":"#9FE1E7","selected":true},
                {"id":"b","summary":"Holidays","backgroundColor":"not-a-colour"},
                {"id":"c","summary":"Gone","deleted":true,"selected":true},
                {"id":"d","summary":"Hidden","selected":true,"hidden":true}
                """),
            tasks: false);

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["a@example.com", "b", "d"], collections.Select(c => c.Id));
        Assert.Equal("My work", collections[0].DisplayName);
        Assert.Equal("#9fe1e7", collections[0].Color);
        Assert.Null(collections[1].Color);
        Assert.Equal([true, false, false], collections.Select(c => c.IsVisible));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.Kind == CollectionKind.Calendar));
    }

    [Fact]
    public async Task A_timed_event_keeps_its_instants_zone_status_and_text()
    {
        var (e, _) = await MapOneAsync("""
            {"id":"e1","status":"tentative","summary":"Standup","description":"line1<br>line2","location":"Room 1","htmlLink":"https://calendar.google.com/event?eid=x",
             "start":{"dateTime":"2026-10-27T09:00:00+01:00","timeZone":"Europe/Warsaw"},"end":{"dateTime":"2026-10-27T09:30:00+01:00"},
             "transparency":"transparent","updated":"2026-10-20T08:00:00.000Z"}
            """);

        Assert.NotNull(e);
        var time = Assert.IsType<TimedEvent>(e.Time);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero), time.Start);
        Assert.Equal(TimeSpan.FromMinutes(30), time.End - time.Start);
        Assert.Equal("Europe/Warsaw", time.TimeZoneId);
        Assert.Equal(EventStatus.Tentative, e.Status);
        Assert.Equal(Availability.Free, e.Availability);
        Assert.Equal("Standup", e.Title);
        Assert.Equal("line1\nline2", e.Description);
        Assert.Equal("Room 1", e.Location);
        Assert.Equal("https://calendar.google.com/event?eid=x", e.Url?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), e.LastModified);
        Assert.Equal(new ItemKey(Setup.SourceId, "cal1", "e1"), e.Key);
    }

    [Fact]
    public async Task An_all_day_event_uses_googles_exclusive_end_date()
    {
        var (e, _) = await MapOneAsync("""{"id":"e1","summary":"Trip","start":{"date":"2026-12-24"},"end":{"date":"2026-12-27"}}""");

        var time = Assert.IsType<AllDayEvent>(e!.Time);
        Assert.Equal(new DateOnly(2026, 12, 24), time.Start);
        Assert.Equal(new DateOnly(2026, 12, 27), time.EndExclusive);
    }

    [Fact]
    public async Task An_event_without_a_title_has_an_empty_title_and_unknown_zone_is_dropped_with_a_warning()
    {
        var (e, result) = await MapOneAsync("""{"id":"e1","start":{"dateTime":"2026-10-27T09:00:00Z","timeZone":"Mars/Olympus"},"end":{"dateTime":"2026-10-27T10:00:00Z"}}""");

        Assert.Equal(string.Empty, e!.Title);
        Assert.Null(((TimedEvent)e.Time).TimeZoneId);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task An_event_with_no_readable_start_is_skipped_with_a_warning_and_the_sync_goes_on()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Events("""{"id":"bad","summary":"x"},{"id":"ok","start":{"date":"2026-10-27"},"end":{"date":"2026-10-28"}}""", sync: "s1"),
            tasks: false);

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.Equal(["ok"], result.Upserts.Select(u => u.Key.ItemId));
        Assert.Equal("bad", Assert.Single(result.Warnings).ItemId);
    }

    [Fact]
    public async Task Working_location_entries_are_dropped_silently()
    {
        var (e, result) = await MapOneAsync("""{"id":"w","eventType":"workingLocation","start":{"date":"2026-10-27"},"end":{"date":"2026-10-28"}}""");

        Assert.Null(e);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("declined", EventStatus.Cancelled)]
    [InlineData("accepted", EventStatus.Confirmed)]
    public async Task My_own_declined_response_hides_the_event_but_someone_elses_does_not(string response, EventStatus expected)
    {
        var (e, _) = await MapOneAsync($$"""
            {"id":"e1","start":{"date":"2026-10-27"},"end":{"date":"2026-10-28"},
             "attendees":[{"self":false,"responseStatus":"declined"},{"self":true,"responseStatus":"{{response}}"}]}
            """);

        Assert.Equal(expected, e!.Status);
    }

    [Fact]
    public async Task A_series_maps_its_rule_excluded_and_extra_dates_in_the_right_zones()
    {
        var (e, result) = await MapOneAsync("""
            {"id":"s1","summary":"Weekly","start":{"dateTime":"2026-10-26T09:00:00+01:00","timeZone":"Europe/Warsaw"},"end":{"dateTime":"2026-10-26T10:00:00+01:00","timeZone":"Europe/Warsaw"},
             "recurrence":["RRULE:FREQ=WEEKLY;BYDAY=MO","EXDATE;TZID=Europe/Warsaw:20261102T090000,20261109T090000","EXDATE:20261116T080000Z","RDATE;VALUE=DATE:20261201","RRULE:FREQ=DAILY"]}
            """);

        var rule = e!.Recurrence!;
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", rule.Rule);
        Assert.Equal(
            [new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)],
            rule.ExcludedDates);
        Assert.Equal([new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)], rule.ExtraDates);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task A_moved_occurrence_is_an_override_keyed_by_its_original_start()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Events("""
                {"id":"s1_20261102T080000Z","recurringEventId":"s1","summary":"Moved","originalStartTime":{"dateTime":"2026-11-02T09:00:00+01:00"},
                 "start":{"dateTime":"2026-11-02T14:00:00+01:00"},"end":{"dateTime":"2026-11-02T15:00:00+01:00"}}
                """, sync: "s1"),
            tasks: false);

        var result = await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        var e = Assert.Single(result.Upserts);
        Assert.Equal("s1", e.Key.ItemId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), e.Key.RecurrenceId);
        Assert.True(e.Key.IsOverride);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.Zero), ((TimedEvent)e.Time).Start);
    }

    [Fact]
    public async Task A_cancelled_occurrence_is_a_cancelled_override_and_not_a_deletion()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Events("""
                {"id":"s1_20261102T080000Z","status":"cancelled","recurringEventId":"s1","originalStartTime":{"dateTime":"2026-11-02T08:00:00Z"}},
                {"id":"s2_20261103","status":"cancelled","recurringEventId":"s2","originalStartTime":{"date":"2026-11-03"}}
                """, sync: "s1"),
            tasks: false);

        var result = await source.SyncEventsAsync(Setup.Calendar(), new SyncState("""{"v":1,"syncToken":"old"}"""), Ct);

        Assert.Empty(result.DeletedItemIds);
        Assert.All(result.Upserts, u => Assert.Equal(EventStatus.Cancelled, u.Status));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), result.Upserts[1].Key.RecurrenceId);
        Assert.IsType<AllDayEvent>(result.Upserts[1].Time);
    }

    [Fact]
    public async Task First_sync_is_a_snapshot_with_a_fixed_parameter_set_and_paging()
    {
        var (handler, source, _, _) = Setup.Create(
            r => Setup.Query(r, "pageToken") == "p2"
                ? Setup.Events("""{"id":"b","start":{"date":"2026-10-28"},"end":{"date":"2026-10-29"}}""", sync: "SYNC1")
                : Setup.Events("""{"id":"a","start":{"date":"2026-10-27"},"end":{"date":"2026-10-28"}}""", next: "p2"),
            tasks: false);

        var result = await source.SyncEventsAsync(Setup.Calendar("a@b.c"), null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["a", "b"], result.Upserts.Select(u => u.Key.ItemId));
        Assert.Contains("SYNC1", result.NewState.Token, StringComparison.Ordinal);
        var calls = handler.ApiRequests.ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c =>
        {
            Assert.Equal("/calendar/v3/calendars/a%40b.c/events", c.RequestUri!.AbsolutePath);
            Assert.Equal("2500", Setup.Query(c, "maxResults"));
            Assert.Equal(string.Empty, Setup.Query(c, "singleEvents"));
            Assert.Equal(string.Empty, Setup.Query(c, "syncToken"));
            Assert.Equal(string.Empty, Setup.Query(c, "timeMin"));
            Assert.Equal(Setup.AccessToken, c.Headers.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task A_delta_sends_the_sync_token_on_every_page_and_reports_deletions()
    {
        var (handler, source, _, _) = Setup.Create(
            r => Setup.Query(r, "pageToken") == "p2"
                ? Setup.Events("""{"id":"gone","status":"cancelled"}""", sync: "SYNC3")
                : Setup.Events("""{"id":"a","summary":"Changed","start":{"date":"2026-10-27"},"end":{"date":"2026-10-28"}}""", next: "p2"),
            tasks: false);

        var result = await source.SyncEventsAsync(Setup.Calendar(), new SyncState("""{"v":1,"syncToken":"SYNC2"}"""), Ct);

        Assert.False(result.IsFullSnapshot);
        Assert.Equal(["a"], result.Upserts.Select(u => u.Key.ItemId));
        Assert.Equal(["gone"], result.DeletedItemIds);
        Assert.All(handler.ApiRequests, c => Assert.Equal("SYNC2", Setup.Query(c, "syncToken")));
        Assert.Contains("SYNC3", result.NewState.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_calendar_is_confirmed_empty_only_for_a_well_formed_reply()
    {
        var (_, good, _, _) = Setup.Create(_ => Setup.Events(string.Empty, sync: "s"), tasks: false);
        Assert.True((await good.SyncEventsAsync(Setup.Calendar(), null, Ct)).ConfirmedEmpty);

        var (_, odd, _, _) = Setup.Create(_ => FakeHandler.Json("""{"nextSyncToken":"s"}"""), tasks: false);
        Assert.False((await odd.SyncEventsAsync(Setup.Calendar(), null, Ct)).ConfirmedEmpty);

        var (_, html, _, _) = Setup.Create(_ => FakeHandler.Json("<html>oops</html>"), tasks: false);
        await Assert.ThrowsAsync<SourceDataException>(() => html.SyncEventsAsync(Setup.Calendar(), null, Ct));
    }

    [Fact]
    public async Task A_reply_without_a_sync_token_is_an_error_not_an_empty_result()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Events(string.Empty), tasks: false);

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncEventsAsync(Setup.Calendar(), null, Ct));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":2,"syncToken":"x"}""")]
    [InlineData("""{"v":1}""")]
    public async Task A_state_that_cannot_be_used_expires(string token)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Events(string.Empty, sync: "s"), tasks: false);

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncEventsAsync(Setup.Calendar(), new SyncState(token), Ct));
    }

    [Fact]
    public async Task Gone_means_the_sync_token_expired()
    {
        var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.Gone), tasks: false);

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncEventsAsync(Setup.Calendar(), new SyncState("""{"v":1,"syncToken":"old"}"""), Ct));
    }

    [Fact]
    public async Task A_missing_calendar_keeps_the_cache_and_warns()
    {
        var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound), tasks: false);
        var previous = new SyncState("""{"v":1,"syncToken":"old"}""");

        var result = await source.SyncEventsAsync(Setup.Calendar(), previous, Ct);

        Assert.Empty(result.Upserts);
        Assert.Empty(result.DeletedItemIds);
        Assert.False(result.IsFullSnapshot);
        Assert.Same(previous, result.NewState);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded", typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.Forbidden, "accessNotConfigured", typeof(SourceException))]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", typeof(SourceAuthException))]
    [InlineData(HttpStatusCode.InternalServerError, "backendError", typeof(SourceUnavailableException))]
    public async Task Errors_map_to_the_exception_the_engine_expects(HttpStatusCode status, string reason, Type expected)
    {
        var (_, source, _, _) = Setup.Create(_ => FakeHandler.Json($$$"""{"error":{"code":{{{(int)status}}},"message":"user@example.com secret","errors":[{"reason":"{{{reason}}}"}]}}""", status), tasks: false);

        var ex = await Assert.ThrowsAnyAsync<SourceException>(() => source.SyncEventsAsync(Setup.Calendar(), null, Ct));

        Assert.Equal(expected, ex.GetType());
        Assert.DoesNotContain("user@example.com", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_gets_one_fresh_access_token_and_then_gives_up_as_an_auth_error()
    {
        var calls = 0;
        var (handler, source, _, _) = Setup.Create(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Setup.Events(string.Empty, sync: "s"), tasks: false);

        await source.SyncEventsAsync(Setup.Calendar(), null, Ct);

        Assert.Equal(2, handler.Requests.Count(r => r.Request.RequestUri!.Host == "oauth2.googleapis.com"));

        var (_, always, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), tasks: false);
        await Assert.ThrowsAsync<SourceAuthException>(() => always.SyncEventsAsync(Setup.Calendar(), null, Ct));
    }

    [Fact]
    public async Task A_redirect_is_never_followed_and_an_address_off_googleapis_is_refused()
    {
        var (handler, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } }, tasks: false);

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncEventsAsync(Setup.Calendar(), null, Ct));

        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.Host == "evil.example");
        using var client = new GoogleClient(handler, _ => Task.FromResult("t"), () => { });
        await Assert.ThrowsAsync<SourceDataException>(() => client.GetAsync(new Uri("https://evil.example/calendar/v3/x"), GoogleJson.Default.ListPageEventDto, Ct));
    }
}
