using System.Net;
using System.Text;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Updates;

namespace AllMyTasks.Core.Tests;

public class UpdateCheckTests
{
    private const string _page = "https://github.com/jkruza/allmytasks/releases/tag/";

    [Theory]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.9.0-alpha", "0.9.0")]
    [InlineData("0.9.0-alpha", "0.9.0-alpha.1")]
    [InlineData("0.9.0-alpha.2", "0.9.0-alpha.10")]
    [InlineData("0.9.0-alpha.1", "0.9.0-alpha.beta")]
    [InlineData("0.9.0-alpha", "0.9.0-beta")]
    [InlineData("0.9.9", "1.0.0-alpha")]
    public void Versions_order_like_semver(string lower, string higher)
    {
        var a = ReleaseVersion.TryParse(lower)!;
        var b = ReleaseVersion.TryParse(higher)!;
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("v0.9.0-alpha", "0.9.0-alpha")]
    [InlineData("0.9.0-alpha+abc123", "0.9.0-alpha")]
    [InlineData("1.2.3", "1.2.3")]
    public void Versions_parse_tags_and_ignore_build_metadata(string text, string expected) =>
        Assert.Equal(expected, ReleaseVersion.TryParse(text)!.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    public void Unreadable_versions_are_null(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public async Task A_newer_prerelease_is_offered_to_a_prerelease_build()
    {
        var (service, _, _) = Create("0.9.0-alpha", Release("0.10.0-alpha", true), Release("0.9.0-alpha", true));

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("0.10.0-alpha", service.Available?.Version);
        Assert.Equal(_page + "v0.10.0-alpha", service.Available?.Url);
    }

    [Fact]
    public async Task A_stable_build_ignores_prereleases()
    {
        var (service, _, _) = Create("1.0.0", Release("1.1.0-alpha", true), Release("1.0.0", false));

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(service.Available);
    }

    [Fact]
    public async Task A_stable_build_is_offered_a_newer_stable_release()
    {
        var (service, _, _) = Create("1.0.0", Release("1.1.0-alpha", true), Release("1.0.1", false));

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("1.0.1", service.Available?.Version);
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_not_offered()
    {
        var (service, _, _) = Create("0.9.0-alpha", Release("0.9.0-alpha", true), Release("0.8.0-alpha", true));

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(service.Available);
    }

    [Fact]
    public async Task A_skipped_version_is_not_offered_again_but_a_newer_one_is()
    {
        var (service, checker, clock) = Create("0.9.0-alpha", Release("0.10.0-alpha", true));
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        await service.SkipAsync("0.10.0-alpha", TestContext.Current.CancellationToken);
        Assert.Null(service.Available);

        clock.Now += TimeSpan.FromHours(25);
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(service.Available);

        checker.Result = new ReleaseCheckResult(ReleaseCheckStatus.Ok, [Release("0.11.0-alpha", true)], "\"b\"");
        clock.Now += TimeSpan.FromHours(25);
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("0.11.0-alpha", service.Available?.Version);
    }

    [Fact]
    public async Task Checks_are_throttled_to_one_per_day_and_use_the_etag()
    {
        var (service, checker, clock) = Create("0.9.0-alpha", Release("0.10.0-alpha", true));

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, checker.Calls);

        clock.Now += TimeSpan.FromHours(23);
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, checker.Calls);

        clock.Now += TimeSpan.FromHours(2);
        checker.Result = ReleaseCheckResult.NotModified;
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, checker.Calls);
        Assert.Equal("\"a\"", checker.LastETag);
        Assert.Equal("0.10.0-alpha", service.Available?.Version);
    }

    [Fact]
    public async Task A_new_service_reads_the_stored_state_without_asking_again()
    {
        var store = new InMemorySettingsStore();
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var first = new FakeChecker(new ReleaseCheckResult(ReleaseCheckStatus.Ok, [Release("0.10.0-alpha", true)], "\"a\""));
        await new UpdateCheckService(store, first, () => true, "0.9.0-alpha", clock).CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        var second = new FakeChecker(ReleaseCheckResult.Failed);
        var restarted = new UpdateCheckService(store, second, () => true, "0.9.0-alpha", clock);
        await restarted.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Calls);
        Assert.Equal("0.10.0-alpha", restarted.Available?.Version);
    }

    [Fact]
    public async Task A_failed_check_is_silent_and_tried_again_next_time()
    {
        var (service, checker, _) = Create("0.9.0-alpha");
        checker.Result = ReleaseCheckResult.Failed;

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(service.Available);
        Assert.Equal(2, checker.Calls);
    }

    [Fact]
    public async Task Turned_off_makes_no_request_and_hides_the_banner()
    {
        var enabled = true;
        var store = new InMemorySettingsStore();
        var checker = new FakeChecker(new ReleaseCheckResult(ReleaseCheckStatus.Ok, [Release("0.10.0-alpha", true)], null));
        var service = new UpdateCheckService(store, checker, () => enabled, "0.9.0-alpha");
        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(service.Available);

        enabled = false;
        await service.CheckAsync(force: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, checker.Calls);
        Assert.Null(service.Available);
    }

    [Fact]
    public async Task An_unreadable_running_version_makes_no_request()
    {
        var checker = new FakeChecker(ReleaseCheckResult.Failed);
        var service = new UpdateCheckService(new InMemorySettingsStore(), checker, () => true, "unknown");

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task The_changed_event_fires_when_an_update_appears()
    {
        var (service, _, _) = Create("0.9.0-alpha", Release("0.10.0-alpha", true));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.CheckAsync(force: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task The_http_checker_sends_an_anonymous_conditional_request_and_reads_releases()
    {
        var handler = new StubHandler(_ => Json("""
            [{"tag_name":"v0.10.0-alpha","html_url":"https://github.com/jkruza/allmytasks/releases/tag/v0.10.0-alpha","prerelease":true,"draft":false},
             {"tag_name":"v0.11.0-alpha","html_url":"https://github.com/jkruza/allmytasks/releases/tag/v0.11.0-alpha","prerelease":true,"draft":true},
             {"tag_name":"v0.12.0","html_url":"https://evil.example/x","prerelease":false},
             {"tag_name":"nightly","html_url":"https://github.com/jkruza/allmytasks/releases/tag/nightly","prerelease":false}]
            """, "\"abc\""));
        using var checker = new GitHubReleaseChecker(handler);

        var result = await checker.CheckAsync("\"old\"", TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCheckStatus.Ok, result.Status);
        Assert.Equal("\"abc\"", result.ETag);
        var only = Assert.Single(result.Releases);
        Assert.Equal("v0.10.0-alpha", only.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("AllMyTasks", request.Headers.UserAgent.ToString());
        Assert.Equal("\"old\"", request.Headers.IfNoneMatch.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotModified, ReleaseCheckStatus.NotModified)]
    [InlineData(HttpStatusCode.Forbidden, ReleaseCheckStatus.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, ReleaseCheckStatus.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, ReleaseCheckStatus.Failed)]
    public async Task The_http_checker_maps_status_codes(HttpStatusCode code, ReleaseCheckStatus expected)
    {
        using var checker = new GitHubReleaseChecker(new StubHandler(_ => new HttpResponseMessage(code)));

        Assert.Equal(expected, (await checker.CheckAsync(null, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task The_http_checker_is_silent_when_offline_or_the_body_is_not_json()
    {
        using var offline = new GitHubReleaseChecker(new StubHandler(_ => throw new HttpRequestException("no network")));
        using var garbage = new GitHubReleaseChecker(new StubHandler(_ => Json("<html>", null)));
        using var wrongShape = new GitHubReleaseChecker(new StubHandler(_ => Json("{}", null)));

        Assert.Equal(ReleaseCheckStatus.Failed, (await offline.CheckAsync(null, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ReleaseCheckStatus.Failed, (await garbage.CheckAsync(null, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ReleaseCheckStatus.Failed, (await wrongShape.CheckAsync(null, TestContext.Current.CancellationToken)).Status);
    }

    private static ReleaseInfo Release(string version, bool prerelease) => new("v" + version, _page + "v" + version, prerelease);

    private static (UpdateCheckService Service, FakeChecker Checker, FixedClock Clock) Create(string current, params ReleaseInfo[] releases)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var checker = new FakeChecker(new ReleaseCheckResult(ReleaseCheckStatus.Ok, releases, "\"a\""));
        return (new UpdateCheckService(new InMemorySettingsStore(), checker, () => true, current, clock), checker, clock);
    }

    private static HttpResponseMessage Json(string body, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    private sealed class FakeChecker(ReleaseCheckResult result) : IReleaseChecker
    {
        public ReleaseCheckResult Result { get; set; } = result;

        public int Calls { get; private set; }

        public string? LastETag { get; private set; }

        public Task<ReleaseCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastETag = etag;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
