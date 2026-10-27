using System.Net;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Providers.Google.Tests;

public class AuthTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static GoogleSource Create(FakeHandler handler, InMemorySecretStore secrets, BrowserLauncher browser, string? clientId = Setup.ClientId, string? ownId = null, string? ownSecret = null, string? builtInSecret = Setup.ClientSecret, bool tasksOnly = false)
    {
        var factory = new GoogleFactory(handler, defaultClientId: clientId, defaultClientSecret: builtInSecret);
        var values = new Dictionary<string, string>();
        if (ownId is not null)
        {
            values[GoogleFactory.ClientIdField] = ownId;
        }

        if (tasksOnly)
        {
            values[GoogleFactory.TasksField] = "true";
        }

        if (ownSecret is not null)
        {
            secrets.SetAsync(Setup.SourceId, GoogleFactory.ClientSecretField, ownSecret).GetAwaiter().GetResult();
        }

        return factory.Create(Setup.SourceId, "Google", new SourceConfig(values), secrets, browser, new ManualTime(Setup.Now));
    }

    private static FakeHandler Handler(Func<string, HttpResponseMessage> token) => new()
    {
        Respond = (request, body) => request.RequestUri!.Host == "oauth2.googleapis.com"
            ? token(body)
            : Setup.Page("""{"id":"l","title":"Tasks"}"""),
    };

    private static string Field(string form, string name) =>
        form.Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p[1])).Single();

    private static Dictionary<string, string> QueryOf(Uri address) =>
        address.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static BrowserLauncher Approving(Action<Uri>? seen = null) => async (address, ct) =>
    {
        seen?.Invoke(address);
        var query = QueryOf(address);
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{query["redirect_uri"]}?code=abc123&state={Uri.EscapeDataString(query["state"])}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    };

    [Fact]
    public async Task First_sign_in_uses_the_browser_with_pkce_and_stores_only_the_refresh_token_and_account()
    {
        var secrets = new InMemorySecretStore();
        Uri? opened = null;
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("refresh_1", idToken: Setup.IdToken("acct-1"))));
        using var source = Create(handler, secrets, Approving(a => opened = a));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.NotEmpty(collections);
        Assert.NotNull(opened);
        Assert.Equal("accounts.google.com", opened.Host);
        var query = QueryOf(opened);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal(Setup.ClientId, query["client_id"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);
        Assert.Contains("calendar.events.readonly", query["scope"], StringComparison.Ordinal);
        Assert.Contains("calendar.calendarlist.readonly", query["scope"], StringComparison.Ordinal);
        Assert.Contains("tasks.readonly", query["scope"], StringComparison.Ordinal);
        Assert.DoesNotContain("auth/calendar ", query["scope"] + " ", StringComparison.Ordinal);
        Assert.Equal("refresh_1", await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
        Assert.Equal("acct-1", await secrets.GetAsync(Setup.SourceId, GoogleAuth.AccountField, Ct));

        var exchange = handler.Requests.Single(r => r.Request.RequestUri!.Host == "oauth2.googleapis.com").Body;
        Assert.Equal("authorization_code", Field(exchange, "grant_type"));
        Assert.Equal("abc123", Field(exchange, "code"));
        Assert.Equal(43 + 21, Field(exchange, "code_verifier").Length);
        Assert.Equal(Setup.ClientSecret, Field(exchange, "client_secret"));
    }

    [Fact]
    public async Task A_source_that_only_reads_tasks_asks_for_no_calendar_permission()
    {
        Uri? opened = null;
        using var source = Create(Handler(_ => FakeHandler.Json(Setup.Token(scopes: $"openid {GoogleAuth.TasksScope}"))), new InMemorySecretStore(), Approving(a => opened = a), tasksOnly: true);

        await source.GetCollectionsAsync(Ct);

        var scope = QueryOf(opened!)["scope"];
        Assert.Contains("tasks.readonly", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar", scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_with_the_wrong_state_is_ignored_and_the_right_one_completes_sign_in()
    {
        var secrets = new InMemorySecretStore();
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("refresh_1")));
        BrowserLauncher browser = async (address, ct) =>
        {
            var query = QueryOf(address);
            using var http = new HttpClient();
            using var forged = await http.GetAsync($"{query["redirect_uri"]}?code=forged&state=wrong", ct);
            Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
            using var real = await http.GetAsync($"{query["redirect_uri"]}?code=real&state={Uri.EscapeDataString(query["state"])}", ct);
        };
        using var source = Create(handler, secrets, browser);

        await source.GetCollectionsAsync(Ct);

        Assert.Equal("real", Field(handler.Requests.Single(r => r.Request.RequestUri!.Host == "oauth2.googleapis.com").Body, "code"));
    }

    [Fact]
    public async Task Declining_in_the_browser_is_an_auth_error_and_nothing_is_stored()
    {
        var secrets = new InMemorySecretStore();
        BrowserLauncher browser = async (address, ct) =>
        {
            var query = QueryOf(address);
            using var http = new HttpClient();
            using var response = await http.GetAsync($"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"])}", ct);
        };
        using var source = Create(Handler(_ => FakeHandler.Json("{}")), secrets, browser);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Null(await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
        Assert.Contains("access_denied", ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unticking_a_permission_fails_the_sign_in_and_stores_nothing()
    {
        var secrets = new InMemorySecretStore();
        using var source = Create(Handler(_ => FakeHandler.Json(Setup.Token(scopes: $"openid {GoogleAuth.TasksScope}"))), secrets, Approving());

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Contains("every permission", ex.Message, StringComparison.Ordinal);
        Assert.Contains("calendar.events.readonly", ex.Diagnostics, StringComparison.Ordinal);
        Assert.Null(await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
    }

    [Fact]
    public async Task Signing_in_as_a_different_account_than_before_is_refused()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.AccountField, "acct-1", Ct);
        using var source = Create(Handler(_ => FakeHandler.Json(Setup.Token(idToken: Setup.IdToken("acct-2")))), secrets, Approving());

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Contains("different Google account", ex.Message, StringComparison.Ordinal);
        Assert.Null(await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
        Assert.Equal("acct-1", await secrets.GetAsync(Setup.SourceId, GoogleAuth.AccountField, Ct));
    }

    [Fact]
    public async Task The_browser_opens_only_once_per_source_instance()
    {
        var opens = 0;
        BrowserLauncher browser = (_, _) =>
        {
            opens++;
            throw new SourceException("No browser.");
        };
        using var source = Create(Handler(_ => FakeHandler.Json("{}")), new InMemorySecretStore(), browser);

        await Assert.ThrowsAsync<SourceException>(() => source.GetCollectionsAsync(Ct));
        var second = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Equal(1, opens);
        Assert.Contains("edit this source", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refresh_stores_a_rotated_token_and_the_access_token_is_reused_until_it_nears_expiry()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, "old", Ct);
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("rotated")));
        using var source = Create(handler, secrets, (_, _) => throw new InvalidOperationException());

        await source.GetCollectionsAsync(Ct);
        await source.GetCollectionsAsync(Ct);

        Assert.Equal("rotated", await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
        var tokenCalls = handler.Requests.Where(r => r.Request.RequestUri!.Host == "oauth2.googleapis.com").ToList();
        Assert.Single(tokenCalls);
        Assert.Equal("refresh_token", Field(tokenCalls[0].Body, "grant_type"));
        Assert.Equal("old", Field(tokenCalls[0].Body, "refresh_token"));
    }

    [Fact]
    public async Task Google_may_omit_the_refresh_token_on_refresh_and_the_stored_one_stays()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, "keep", Ct);
        using var source = Create(Handler(_ => FakeHandler.Json($$"""{"access_token":"{{Setup.AccessToken}}","expires_in":3600}""")), secrets, (_, _) => throw new InvalidOperationException());

        await source.GetCollectionsAsync(Ct);

        Assert.Equal("keep", await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
    }

    [Fact]
    public async Task Concurrent_calls_share_one_refresh()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, "r", Ct);
        var handler = Handler(_ => FakeHandler.Json(Setup.Token()));
        using var source = Create(handler, secrets, (_, _) => throw new InvalidOperationException());

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetCollectionsAsync(Ct)));

        Assert.Single(handler.Requests, r => r.Request.RequestUri!.Host == "oauth2.googleapis.com");
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_deleted_and_asks_the_user_to_sign_in_again_without_leaking_it()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Setup.RefreshToken, Ct);
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.AccountField, "acct-1", Ct);
        var handler = Handler(_ => FakeHandler.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked for user@example.com."}""", HttpStatusCode.BadRequest));
        using var source = Create(handler, secrets, (_, _) => throw new InvalidOperationException());

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Null(await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
        Assert.Equal("acct-1", await secrets.GetAsync(Setup.SourceId, GoogleAuth.AccountField, Ct));
        var text = ex.Message + ex.Diagnostics;
        Assert.DoesNotContain(Setup.RefreshToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_busy_token_endpoint_is_unavailable_not_an_auth_error()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, "r", Ct);
        using var source = Create(Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), secrets, (_, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.GetCollectionsAsync(Ct));

        Assert.Equal("r", await secrets.GetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, Ct));
    }

    [Fact]
    public async Task Without_any_client_id_the_source_says_how_to_get_one_and_does_not_open_a_browser()
    {
        using var source = Create(Handler(_ => FakeHandler.Json("{}")), new InMemorySecretStore(), (_, _) => throw new InvalidOperationException("opened"), clientId: null);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Contains("client", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_client_of_the_users_own_uses_the_users_own_secret_and_never_the_built_in_one()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, GoogleAuth.RefreshTokenField, "r", Ct);
        var handler = Handler(_ => FakeHandler.Json(Setup.Token()));
        using var own = Create(handler, secrets, (_, _) => Task.CompletedTask, ownId: "mine.apps.googleusercontent.com", ownSecret: "mine_secret");

        await own.GetCollectionsAsync(Ct);

        var body = handler.Requests[0].Body;
        Assert.Equal("mine.apps.googleusercontent.com", Field(body, "client_id"));
        Assert.Equal("mine_secret", Field(body, "client_secret"));
        Assert.DoesNotContain(Setup.ClientSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_and_secrets_never_appear_in_an_error_from_the_api()
    {
        var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), calendar: false);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        var text = ex + ex.Diagnostics;
        Assert.DoesNotContain(Setup.AccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Setup.RefreshToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Setup.ClientSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bearer_token_goes_only_to_the_api_hosts()
    {
        var (handler, source, _, _) = Setup.Create(_ => Setup.Page("""{"id":"l","title":"Tasks","selected":true}"""));

        await source.GetCollectionsAsync(Ct);

        Assert.All(handler.ApiRequests, r => Assert.Equal(Setup.AccessToken, r.Headers.Authorization?.Parameter));
        Assert.All(handler.Requests.Where(r => r.Request.RequestUri!.Host == "oauth2.googleapis.com"), r => Assert.Null(r.Request.Headers.Authorization));
    }

    [Fact]
    public void The_id_token_subject_is_read_and_garbage_is_ignored()
    {
        Assert.Equal("abc", GoogleAuth.SubjectOf(Setup.IdToken("abc")));
        Assert.Null(GoogleAuth.SubjectOf("x.y.z"));
        Assert.Null(GoogleAuth.SubjectOf("nope"));
        Assert.Null(GoogleAuth.SubjectOf(null));
    }

    [Fact]
    public void The_schema_offers_client_credentials_both_services_and_the_refresh_interval()
    {
        var factory = new GoogleFactory(new FakeHandler());

        Assert.Equal("google", factory.Kind);
        Assert.Equal([GoogleFactory.ClientIdField, GoogleFactory.ClientSecretField, GoogleFactory.CalendarField, GoogleFactory.TasksField, "refreshMinutes"], factory.ConfigSchema.Fields.Select(f => f.Key));
        Assert.Equal(FieldKind.Password, factory.ConfigSchema.Fields.Single(f => f.Key == GoogleFactory.ClientSecretField).Kind);
    }
}
