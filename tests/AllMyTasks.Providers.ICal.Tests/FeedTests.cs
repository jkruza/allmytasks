using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Providers.ICal.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

public class UrlTests
{
    [Theory]
    [InlineData("https://user:pw@example.com/private/abc123/cal.ics?token=SECRET", "https://example.com")]
    [InlineData("http://example.com:8080/x", "http://example.com")]
    [InlineData("nonsense", "(invalid address)")]
    [InlineData(null, "(invalid address)")]
    public void Redaction_keeps_only_scheme_and_host(string? url, string expected)
    {
        var redacted = UrlRedactor.Redact(url);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webcal://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("webcals://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("https://example.com/a.ics?x=1", "https://example.com/a.ics?x=1")]
    [InlineData("http://example.com/a.ics", "http://example.com/a.ics")]
    [InlineData("ftp://example.com/a.ics", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void Feed_urls_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, UrlRedactor.NormalizeFeedUrl(input)?.ToString());
}

public class ClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Uri Feed = new("https://example.com/feed.ics");

    private static HttpResponseMessage Ok(string body, string? etag = null, string? contentType = "text/calendar; charset=utf-8")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        if (contentType is not null)
        {
            r.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (etag is not null)
        {
            r.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return r;
    }

    private static IcalFeedClient Client(FakeHandler h, long max = IcalFeedClient.DefaultMaxBytes, TimeSpan? timeout = null) => new(h, max, timeout);

    [Fact]
    public async Task Fetch_returns_text_validators_and_hash_and_sends_conditional_headers()
    {
        var handler = new FakeHandler { Respond = _ => Ok("BEGIN:VCALENDAR", "\"v1\"") };
        using var client = Client(handler);

        var first = await client.FetchAsync(Feed, null, Ct);
        await client.FetchAsync(Feed, new FeedValidators(first.ETag, "Mon, 19 Oct 2026 09:00:00 GMT"), Ct);

        Assert.Equal("BEGIN:VCALENDAR", first.Text);
        Assert.Equal("\"v1\"", first.ETag);
        Assert.NotEmpty(first.Hash);
        Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.GetValues("If-None-Match").Single());
        Assert.Equal("Mon, 19 Oct 2026 09:00:00 GMT", handler.Requests[1].Headers.GetValues("If-Modified-Since").Single());
    }

    [Fact]
    public async Task Not_modified_is_reported()
    {
        using var client = Client(new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified) });

        Assert.True((await client.FetchAsync(Feed, new FeedValidators("\"v1\"", null), Ct)).NotModified);
    }

    [Fact]
    public async Task Redirects_are_followed_but_never_from_https_to_http()
    {
        var handler = new FakeHandler();
        handler.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/feed.ics" => Redirect("https://other.example.com/new.ics"),
            "/new.ics" => Ok("BEGIN:VCALENDAR"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var client = Client(handler);

        Assert.Equal("BEGIN:VCALENDAR", (await client.FetchAsync(Feed, null, Ct)).Text);
        Assert.Equal("other.example.com", handler.Requests[1].RequestUri!.Host);

        handler.Respond = _ => Redirect("http://example.com/insecure.ics");
        await Assert.ThrowsAsync<SourceDataException>(() => client.FetchAsync(Feed, null, Ct));
    }

    [Fact]
    public async Task Relative_redirects_resolve_and_loops_end()
    {
        var handler = new FakeHandler();
        var hops = 0;
        handler.Respond = r => r.RequestUri!.AbsolutePath == "/loop" ? Redirect("/loop") : ++hops == 1 ? Redirect("/loop") : Ok("x");
        using var client = Client(handler);

        await Assert.ThrowsAsync<SourceDataException>(() => client.FetchAsync(Feed, null, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(SourceAuthException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(SourceAuthException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(SourceUnavailableException))]
    [InlineData(HttpStatusCode.NotFound, typeof(SourceException))]
    public async Task Http_errors_map_to_typed_exceptions_without_the_url(HttpStatusCode status, Type expected)
    {
        using var client = Client(new FakeHandler { Respond = _ => new HttpResponseMessage(status) });

        var ex = await Assert.ThrowsAnyAsync<SourceException>(() => client.FetchAsync(new Uri("https://example.com/secret-token-123/feed.ics"), null, Ct));

        Assert.Equal(expected, ex.GetType());
        Assert.DoesNotContain("secret-token-123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_failures_become_unavailable_without_leaking_the_address()
    {
        using var client = Client(new FakeHandler { Respond = _ => throw new HttpRequestException("Name or service not known (secret.example.com)") });

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => client.FetchAsync(Feed, null, Ct));

        Assert.DoesNotContain("secret.example.com", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_by_declared_and_by_actual_size()
    {
        using var client = Client(new FakeHandler { Respond = _ => Ok(new string('x', 2000)) }, max: 1000);
        await Assert.ThrowsAsync<SourceDataException>(() => client.FetchAsync(Feed, null, Ct));

        var streaming = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) },
        };
        using var client2 = Client(streaming, max: 1000);
        await Assert.ThrowsAsync<SourceDataException>(() => client2.FetchAsync(Feed, null, Ct));
    }

    [Fact]
    public async Task A_slow_server_times_out_as_unavailable_but_caller_cancellation_propagates()
    {
        var slow = new SlowHandler();
        using var client = new IcalFeedClient(slow, timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => client.FetchAsync(Feed, null, Ct));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(Feed, null, cts.Token));
    }

    [Fact]
    public void Decoding_handles_bom_declared_charset_and_invalid_utf8()
    {
        Assert.Equal("héllo", IcalFeedClient.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("héllo")], null));
        Assert.Equal("héllo", IcalFeedClient.Decode(Encoding.Latin1.GetBytes("héllo"), MediaTypeHeaderValue.Parse("text/calendar; charset=iso-8859-1")));
        Assert.Equal("héllo", IcalFeedClient.Decode(Encoding.Latin1.GetBytes("héllo"), null));
        Assert.Equal("héllo", IcalFeedClient.Decode(Encoding.UTF8.GetBytes("héllo"), MediaTypeHeaderValue.Parse("text/calendar; charset=nonsense")));
        Assert.Equal("hi", IcalFeedClient.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hi")], null));
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public class SourceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Secret = "https://example.com/private/TOP-SECRET-TOKEN/cal.ics";

    private static (IcalFeedSource Source, FakeHandler Handler, CollectionInfo Collection) Create(string? url = Secret)
    {
        var handler = new FakeHandler();
        var client = new IcalFeedClient(handler);
        var source = new IcalFeedSource("ical-1", "My feed", client, _ => Task.FromResult(url));
        return (source, handler, new CollectionInfo("ical-1", "feed", "My feed", CollectionKind.Calendar, IsReadOnly: true));
    }

    private static HttpResponseMessage Feed(string body, string etag = "\"e1\"")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        r.Headers.ETag = new EntityTagHeaderValue(etag);
        return r;
    }

    [Fact]
    public async Task First_sync_is_a_confirmed_snapshot_with_state_and_warnings()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed(Fixtures.Wrap(Fixtures.Malformed, "X-WR-CALNAME:Team\r\n"));

        var result = await source.SyncEventsAsync(collection, null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["good-1", "good-2"], result.Upserts.Select(e => e.Key.ItemId).Order());
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("e1", result.NewState.Token, StringComparison.Ordinal);
        Assert.Equal("Team", (await source.GetCollectionsAsync(Ct)).Single().DisplayName);
    }

    [Fact]
    public async Task Collection_is_one_read_only_calendar_named_after_the_source_until_the_feed_says_otherwise()
    {
        var (source, _, _) = Create();

        var c = Assert.Single(await source.GetCollectionsAsync(Ct));

        Assert.Equal(("feed", "My feed", CollectionKind.Calendar, true), (c.Id, c.DisplayName, c.Kind, c.IsReadOnly));
    }

    [Fact]
    public async Task Second_sync_uses_validators_and_a_304_is_an_empty_delta_with_the_same_state()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed(Fixtures.Wrap(Fixtures.Basic));
        var first = await source.SyncEventsAsync(collection, null, Ct);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);

        var second = await source.SyncEventsAsync(collection, first.NewState, Ct);

        Assert.False(second.IsFullSnapshot);
        Assert.Empty(second.Upserts);
        Assert.Equal(first.NewState, second.NewState);
        Assert.Equal("\"e1\"", handler.Requests[^1].Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task Same_bytes_without_validators_are_a_no_op_delta()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Wrap(Fixtures.Basic)) };
        var first = await source.SyncEventsAsync(collection, null, Ct);

        var second = await source.SyncEventsAsync(collection, first.NewState, Ct);

        Assert.False(second.IsFullSnapshot);
        Assert.Empty(second.Upserts);
    }

    [Fact]
    public async Task Changed_content_gives_a_new_snapshot()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed(Fixtures.Wrap(Fixtures.Basic), "\"e1\"");
        var first = await source.SyncEventsAsync(collection, null, Ct);
        handler.Respond = _ => Feed(Fixtures.Wrap(Fixtures.NoUid), "\"e2\"");

        var second = await source.SyncEventsAsync(collection, first.NewState, Ct);

        Assert.True(second.IsFullSnapshot);
        Assert.Single(second.Upserts);
    }

    [Fact]
    public async Task An_error_page_is_a_data_error_never_an_empty_snapshot()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed("<html><body>Maintenance</body></html>");

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncEventsAsync(collection, null, Ct));
    }

    [Fact]
    public async Task A_real_empty_calendar_is_a_confirmed_empty_snapshot()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed(Fixtures.Wrap(string.Empty));

        var result = await source.SyncEventsAsync(collection, null, Ct);

        Assert.Empty(result.Upserts);
        Assert.True(result.ConfirmedEmpty);
    }

    [Fact]
    public async Task Missing_or_invalid_address_asks_the_user_and_never_echoes_it()
    {
        foreach (var url in new string?[] { null, "", "ftp://x/y" })
        {
            var (source, _, collection) = Create(url);

            var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.SyncEventsAsync(collection, null, Ct));

            Assert.DoesNotContain("ftp", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_feed_address_never_appears_in_errors_or_tostring()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => throw new HttpRequestException(Secret);

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncEventsAsync(collection, null, Ct));

        Assert.DoesNotContain("TOP-SECRET-TOKEN", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-TOKEN", source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-TOKEN", collection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_state_is_treated_as_a_first_sync()
    {
        var (source, handler, collection) = Create();
        handler.Respond = _ => Feed(Fixtures.Wrap(Fixtures.Basic));

        var result = await source.SyncEventsAsync(collection, new SyncState("not json"), Ct);

        Assert.True(result.IsFullSnapshot);
    }
}

public class FactoryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public void Schema_asks_for_a_secret_url_and_an_optional_interval()
    {
        using var factory = new IcalFeedFactory(new FakeHandler());

        var url = factory.ConfigSchema.Fields.Single(f => f.Key == "url");
        Assert.True(url.Required);
        Assert.True(url.IsSecretField);
        Assert.Equal(FieldKind.Url, url.Kind);
        Assert.Equal(FieldKind.Number, factory.ConfigSchema.Fields.Single(f => f.Key == SyncScheduler.IntervalKey).Kind);
        Assert.Equal("ical", factory.Kind);
    }

    [Fact]
    public async Task Added_through_the_registry_the_url_is_kept_as_a_secret_not_in_config()
    {
        using var factory = new IcalFeedFactory(new FakeHandler());
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        var registry = new SourceRegistry([factory], store, secrets);

        var info = await registry.AddAsync("ical", "Holidays", new Dictionary<string, string> { ["url"] = "webcal://example.com/h.ics", ["refreshMinutes"] = "60" }, Ct);

        Assert.Equal("webcal://example.com/h.ics", await secrets.GetAsync(info.Id, "url", Ct));
        var config = (await store.GetSourceConfigAsync(info.Id, Ct))!;
        Assert.Equal(["refreshMinutes"], config.Values.Keys);
        Assert.Equal(TimeSpan.FromMinutes(60), SyncScheduler.IntervalFor(config));
    }

    [Fact]
    public async Task End_to_end_through_the_engine_a_feed_fills_the_cache_and_a_304_keeps_it()
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Wrap(Fixtures.Basic + "\n" + Fixtures.Recurring)) } };
        using var factory = new IcalFeedFactory(handler);
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        var registry = new SourceRegistry([factory], store, secrets);
        var info = await registry.AddAsync("ical", "Feed", new Dictionary<string, string> { ["url"] = "https://example.com/a.ics" }, Ct);
        using var engine = new SyncEngine(store, store, registry, TimeProvider.System);

        await engine.SyncSourceAsync(info.Id, Ct);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        await engine.SyncSourceAsync(info.Id, Ct);

        var status = (await engine.GetStatusesAsync(Ct)).Single();
        Assert.Equal(SourceStatusKind.Ok, status.Kind);
        var events = await store.GetEventCandidatesAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, Ct);
        Assert.Contains(events, e => e.Title == "UTC meeting");
        Assert.Equal("Feed", (await store.GetCollectionsAsync(Ct)).Single().DisplayName);
    }
}
