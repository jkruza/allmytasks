using System.Net;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Providers.ICal.CalDav;

namespace AllMyTasks.Providers.ICal.Tests;

public class CalDavTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string Event(string uid, string summary = "Meeting", int day = 19) =>
        Fixtures.Wrap($"BEGIN:VEVENT\nUID:{uid}\nDTSTAMP:20261001T000000Z\nDTSTART:202610{day:00}T090000Z\nDTEND:202610{day:00}T100000Z\nSUMMARY:{summary}\nEND:VEVENT");

    private static (FakeCalDavServer Server, CalDavSource Source, InMemorySecretStore Secrets) Create(Action<FakeCalDavServer>? configure = null, string url = "https://dav.example.com/", bool insecure = false)
    {
        var server = new FakeCalDavServer();
        configure?.Invoke(server);
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("dav-1", "password", "pw", Ct).GetAwaiter().GetResult();
        var factory = new CalDavFactory(server);
        var config = new SourceConfig(new Dictionary<string, string>
        {
            ["url"] = url,
            ["username"] = "me",
            ["allowInsecure"] = insecure ? "true" : "false",
        });
        return (server, (CalDavSource)factory.Create("dav-1", "My server", config, secrets), secrets);
    }

    private static async Task<(CollectionInfo Info, SyncResult<CalendarEvent> Result)> SyncFirstAsync(CalDavSource source, string path)
    {
        var info = (await source.GetCollectionsAsync(Ct)).Single(c => c.Id == path);
        return (info, await source.SyncEventsAsync(info, null, Ct));
    }

    [Fact]
    public async Task Discovery_follows_principal_then_home_and_lists_calendars_with_names_and_colours()
    {
        var (server, source, _) = Create(s =>
        {
            s.Add("work", "Work").Put("a.ics", Event("a"));
            s.Add("home", "Home").Color = null;
            s.Add("tasks", "Todo").Components = "VTODO";
        });

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["Home", "Work"], collections.Select(c => c.DisplayName).Order());
        var work = collections.Single(c => c.DisplayName == "Work");
        Assert.Equal(("/dav/cal/work/", "#ff8800", CollectionKind.Calendar, true), (work.Id, work.Color, work.Kind, work.IsReadOnly));
        Assert.Null(collections.Single(c => c.DisplayName == "Home").Color);
        Assert.All(collections, c => Assert.Equal("dav-1", c.SourceId));
        Assert.Equal(3, server.Count("propfind"));
    }

    [Fact]
    public async Task A_bare_domain_that_fails_is_retried_at_well_known()
    {
        var (server, source, _) = Create(s =>
        {
            s.WellKnownOnly = true;
            s.Add("work", "Work");
        });

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Single(collections);
        Assert.Contains(server.Requests, r => r.Path == "/.well-known/caldav");
    }

    [Fact]
    public async Task An_address_that_is_itself_a_calendar_is_listed_directly()
    {
        var (_, source, _) = Create(s =>
        {
            s.AddressIsCalendar = true;
            s.Add("work", "Work");
        }, url: "https://dav.example.com/dav/cal/work/");

        var collection = Assert.Single(await source.GetCollectionsAsync(Ct));

        Assert.Equal("/dav/cal/work/", collection.Id);
    }

    [Fact]
    public async Task First_sync_with_sync_collection_is_a_snapshot_and_second_sync_a_delta_with_deletions()
    {
        var (server, source, _) = Create(s =>
        {
            var c = s.Add("work", "Work");
            c.Put("a.ics", Event("uid-a", "A"));
            c.Put("b.ics", Event("uid-b", "B", 20));
        });
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var work = server.Calendars["/dav/cal/work/"];
        work.Put("c.ics", Event("uid-c", "C", 21));
        work.Put("a.ics", Event("uid-a", "A changed"));
        work.Delete("b.ics");

        var second = await source.SyncEventsAsync(info, first.NewState, Ct);

        Assert.True(first.IsFullSnapshot);
        Assert.Equal(["A", "B"], first.Upserts.Select(e => e.Title).Order());
        Assert.False(second.IsFullSnapshot);
        Assert.Equal(["A changed", "C"], second.Upserts.Select(e => e.Title).Order());
        Assert.Equal(["uid-b"], second.DeletedItemIds);
        Assert.Equal(2, server.Count("sync-collection"));
        Assert.Equal(0, server.Count("calendar-query"));
    }

    [Fact]
    public async Task Sync_collection_with_no_changes_fetches_no_bodies()
    {
        var (server, source, _) = Create(s => s.Add("work", "Work").Put("a.ics", Event("a")));
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var multigets = server.Count("calendar-multiget");

        var second = await source.SyncEventsAsync(info, first.NewState, Ct);

        Assert.Empty(second.Upserts);
        Assert.Empty(second.DeletedItemIds);
        Assert.Equal(multigets, server.Count("calendar-multiget"));
    }

    [Fact]
    public async Task An_expired_sync_token_is_reported_so_the_engine_resyncs_from_scratch()
    {
        var (server, source, _) = Create(s => s.Add("work", "Work").Put("a.ics", Event("a")));
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var work = server.Calendars["/dav/cal/work/"];
        work.Put("b.ics", Event("b"));
        work.MinValidVersion = work.Version + 10;

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncEventsAsync(info, first.NewState, Ct));

        var again = await source.SyncEventsAsync(info, null, Ct);
        Assert.True(again.IsFullSnapshot);
        Assert.Equal(2, again.Upserts.Count);
    }

    [Fact]
    public async Task Without_sync_collection_the_ctag_and_etag_diff_is_used()
    {
        var (server, source, _) = Create(s =>
        {
            s.SupportsSyncCollection = false;
            var c = s.Add("work", "Work");
            c.Put("a.ics", Event("uid-a", "A"));
            c.Put("b.ics", Event("uid-b", "B", 20));
        });
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var unchanged = await source.SyncEventsAsync(info, first.NewState, Ct);
        var work = server.Calendars["/dav/cal/work/"];
        work.Put("a.ics", Event("uid-a", "A2"));
        work.Delete("b.ics");
        work.Put("c.ics", Event("uid-c", "C", 22));
        var multigetsBefore = server.Count("calendar-multiget");

        var changed = await source.SyncEventsAsync(info, first.NewState, Ct);

        Assert.True(first.IsFullSnapshot);
        Assert.Empty(unchanged.Upserts);
        Assert.Equal(0, server.Count("sync-collection") - server.Requests.Count(r => r.Kind == "sync-collection"));
        Assert.Equal(["A2", "C"], changed.Upserts.Select(e => e.Title).Order());
        Assert.Equal(["uid-b"], changed.DeletedItemIds);
        Assert.Equal(multigetsBefore + 1, server.Count("calendar-multiget"));
    }

    [Fact]
    public async Task Unchanged_ctag_means_no_listing_or_fetching()
    {
        var (server, source, _) = Create(s =>
        {
            s.SupportsSyncCollection = false;
            s.Add("work", "Work").Put("a.ics", Event("a"));
        });
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var before = server.Requests.Count;

        var second = await source.SyncEventsAsync(info, first.NewState, Ct);

        Assert.Empty(second.Upserts);
        Assert.Equal(before + 1, server.Requests.Count);
    }

    [Fact]
    public async Task Without_ctag_or_sync_a_full_query_is_the_last_resort_and_unchanged_etags_write_nothing()
    {
        var (server, source, _) = Create(s =>
        {
            s.SupportsSyncCollection = false;
            s.SupportsCTag = false;
            s.Add("work", "Work").Put("a.ics", Event("a"));
        });
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");

        var second = await source.SyncEventsAsync(info, first.NewState, Ct);
        server.Calendars["/dav/cal/work/"].Put("b.ics", Event("b"));
        var third = await source.SyncEventsAsync(info, second.NewState, Ct);

        Assert.True(first.IsFullSnapshot);
        Assert.False(second.IsFullSnapshot);
        Assert.True(third.IsFullSnapshot);
        Assert.Equal(2, third.Upserts.Count);
        Assert.Equal(3, server.Count("calendar-query"));
    }

    [Fact]
    public async Task A_server_that_advertises_but_rejects_sync_collection_falls_back()
    {
        var (server, source, _) = Create(s => s.Add("work", "Work").Put("a.ics", Event("a")));
        var info = (await source.GetCollectionsAsync(Ct)).Single();
        server.SupportsSyncCollection = false;

        var result = await source.SyncEventsAsync(info, null, Ct);

        Assert.Single(result.Upserts);
        Assert.Equal(1, server.Count("sync-collection"));
    }

    [Fact]
    public async Task Recurring_events_with_overrides_in_one_resource_are_mapped_together()
    {
        var ics = Fixtures.Wrap(Fixtures.Recurring);
        var (_, source, _) = Create(s => s.Add("work", "Work").Put("series.ics", ics));

        var (_, result) = await SyncFirstAsync(source, "/dav/cal/work/");

        Assert.Equal(3, result.Upserts.Count(e => e.Key.ItemId == "weekly-1"));
        Assert.Contains(result.Upserts, e => e.Key.IsOverride && e.Key.CollectionId == "/dav/cal/work/");
    }

    [Fact]
    public async Task A_resource_without_uid_gets_an_id_derived_from_its_href_and_a_bad_resource_is_skipped()
    {
        var noUid = Fixtures.Wrap(Fixtures.NoUid);
        var (_, source, _) = Create(s =>
        {
            var c = s.Add("work", "Work");
            c.Put("nouid.ics", noUid);
            c.Put("junk.ics", "<html>oops</html>");
            c.Put("ok.ics", Event("ok"));
        });

        var (_, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        var (_, again) = await SyncFirstAsync(source, "/dav/cal/work/");

        Assert.Equal(new[] { "ok", first.Upserts.Single(e => e.Title == "No uid").Key.ItemId }.Order(), first.Upserts.Select(e => e.Key.ItemId).Order());
        Assert.StartsWith("noid-", first.Upserts.Single(e => e.Title == "No uid").Key.ItemId, StringComparison.Ordinal);
        Assert.Equal(first.Upserts.Single(e => e.Title == "No uid").Key.ItemId, again.Upserts.Single(e => e.Title == "No uid").Key.ItemId);
        Assert.NotEmpty(first.Warnings);
    }

    [Fact]
    public async Task Wrong_credentials_are_an_auth_error_and_the_password_is_never_in_the_message()
    {
        var (_, source, secrets) = Create(s => s.Password = "something-else");
        await secrets.SetAsync("dav-1", "password", "my-secret-pw", Ct);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.DoesNotContain("my-secret-pw", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret-pw", source.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Basic_credentials_go_only_over_https_unless_the_user_allowed_http()
    {
        var (server, source, _) = Create(s => s.Add("work", "Work"), url: "http://dav.example.com/");

        await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));
        Assert.Empty(server.Requests);

        var (server2, source2, _) = Create(s => s.Add("work", "Work"), url: "http://dav.example.com/", insecure: true);
        Assert.Single(await source2.GetCollectionsAsync(Ct));
        Assert.StartsWith("Basic ", server2.LastAuthorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_collection_keeps_the_cache_and_warns_instead_of_failing()
    {
        var (server, source, _) = Create(s => s.Add("work", "Work").Put("a.ics", Event("a")));
        var (info, first) = await SyncFirstAsync(source, "/dav/cal/work/");
        server.Calendars.Remove("/dav/cal/work/");

        var result = await source.SyncEventsAsync(info, first.NewState, Ct);

        Assert.False(result.IsFullSnapshot);
        Assert.Empty(result.Upserts);
        Assert.Empty(result.DeletedItemIds);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(first.NewState, result.NewState);
    }

    [Fact]
    public async Task Hrefs_may_be_absolute_urls_or_percent_encoded()
    {
        Assert.Equal("/dav/cal/my%20cal/a.ics", CalDavSource.PathOf(new Uri("https://dav.example.com/dav/"), "https://dav.example.com/dav/cal/my%20cal/a.ics"));
        Assert.Equal("/dav/cal/x.ics", CalDavSource.PathOf(new Uri("https://dav.example.com/dav/cal/"), "/dav/cal/x.ics"));
        Assert.Equal("#aabbcc", CalDavSource.NormalizeColor("#AABBCCFF"));
        Assert.Equal("#aabbcc", CalDavSource.NormalizeColor("aabbcc"));
        Assert.Null(CalDavSource.NormalizeColor("red"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Redirects_never_change_host_or_downgrade_scheme()
    {
        var origin = new Uri("https://dav.example.com/");

        Assert.Equal("https://dav.example.com/dav/", CalDavClient.NextHop(origin, origin, new Uri("/dav/", UriKind.Relative)).ToString());
        Assert.Throws<SourceDataException>(() => CalDavClient.NextHop(origin, origin, new Uri("https://evil.example.net/")));
        Assert.Throws<SourceDataException>(() => CalDavClient.NextHop(origin, origin, new Uri("http://dav.example.com/")));
        Assert.Throws<SourceDataException>(() => CalDavClient.NextHop(origin, origin, null));
    }

    [Fact]
    public void Multistatus_parsing_ignores_dtd_and_rejects_non_xml()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>&x;</d:href></d:response></d:multistatus>";

        Assert.Throws<SourceDataException>(() => DavXml.ParseMultistatus(xxe));
        Assert.Throws<SourceDataException>(() => DavXml.ParseMultistatus("not xml"));
        Assert.Throws<SourceDataException>(() => DavXml.ParseMultistatus("<html/>"));
    }

    [Fact]
    public void Schema_asks_for_server_user_and_a_secret_password_and_an_insecure_switch()
    {
        using var factory = new CalDavFactory(new FakeCalDavServer());

        var fields = factory.ConfigSchema.Fields.ToDictionary(f => f.Key);
        Assert.True(fields["url"].Required);
        Assert.False(fields["url"].IsSecretField);
        Assert.True(fields["password"].IsSecretField);
        Assert.Equal(FieldKind.Bool, fields["allowInsecure"].Kind);
        Assert.Equal("caldav", factory.Kind);
    }

    [Fact]
    public async Task Through_the_registry_and_engine_a_server_fills_the_cache_and_hidden_collections_are_not_synced()
    {
        var server = new FakeCalDavServer();
        server.Add("work", "Work").Put("a.ics", Event("a", "Work meeting"));
        server.Add("home", "Home").Put("b.ics", Event("b", "Home thing", 20));
        using var factory = new CalDavFactory(server);
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        var registry = new SourceRegistry([factory], store, secrets);
        var info = await registry.AddAsync("caldav", "Server", new Dictionary<string, string> { ["url"] = "https://dav.example.com", ["username"] = "me", ["password"] = "pw" }, Ct);
        using var engine = new SyncEngine(store, store, registry, TimeProvider.System);

        await engine.SyncSourceAsync(info.Id, Ct);
        var home = (await store.GetCollectionsAsync(Ct)).Single(c => c.DisplayName == "Home");
        await store.SetCollectionPreferencesAsync(info.Id, home.Id, false, "#123456", Ct);
        server.Calendars["/dav/cal/home/"].Put("c.ics", Event("c", "New home thing", 21));
        server.Calendars["/dav/cal/work/"].Put("d.ics", Event("d", "New work thing", 22));
        await engine.SyncSourceAsync(info.Id, Ct);

        Assert.Equal(SourceStatusKind.Ok, (await engine.GetStatusesAsync(Ct)).Single().Kind);
        var events = await store.GetEventCandidatesAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, Ct);
        Assert.Contains(events, e => e.Title == "New work thing");
        Assert.DoesNotContain(events, e => e.Title == "New home thing");
        Assert.Contains(events, e => e.Title == "Home thing");
        Assert.Equal("#123456", (await store.GetCollectionsAsync(Ct)).Single(c => c.DisplayName == "Home").Color);
        Assert.Equal("pw", await secrets.GetAsync(info.Id, "password", Ct));
    }

    [Fact]
    public async Task Server_errors_map_to_unavailable()
    {
        var handler = new FakeStatusHandler(HttpStatusCode.BadGateway);
        using var factory = new CalDavFactory(handler);
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("d", "password", "pw", Ct);
        var source = (CalDavSource)factory.Create("d", "D", new SourceConfig(new Dictionary<string, string> { ["url"] = "https://dav.example.com", ["username"] = "me" }), secrets);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.GetCollectionsAsync(Ct));
    }

    private sealed class FakeStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
