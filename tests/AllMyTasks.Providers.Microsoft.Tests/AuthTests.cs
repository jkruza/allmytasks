using System.Net;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Providers.Microsoft.Tests;

public class AuthTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static MicrosoftSource Create(FakeHandler handler, InMemorySecretStore secrets, BrowserLauncher browser, string? clientId = Setup.ClientId, string? ownId = null)
    {
        var factory = new MicrosoftFactory(handler, defaultClientId: clientId);
        var config = new SourceConfig(ownId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [MicrosoftFactory.ClientIdField] = ownId });
        return factory.Create(Setup.SourceId, "Microsoft", config, secrets, browser, new ManualTime(Setup.Now));
    }

    private static FakeHandler Handler(Func<string, HttpResponseMessage> token) => new()
    {
        Respond = (request, body) => request.RequestUri!.Host == "login.microsoftonline.com"
            ? token(body)
            : Setup.Page("""{"id":"l","displayName":"Tasks"}"""),
    };

    private static string Field(string form, string name) =>
        form.Split('&').Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p[1])).Single();

    [Fact]
    public async Task First_sign_in_uses_the_browser_with_pkce_and_stores_only_the_refresh_token()
    {
        var secrets = new InMemorySecretStore();
        Uri? opened = null;
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("refresh_1")));
        BrowserLauncher browser = async (address, ct) =>
        {
            opened = address;
            var query = address.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            using var http = new HttpClient();
            using var response = await http.GetAsync($"{query["redirect_uri"]}?code=abc123&state={Uri.EscapeDataString(query["state"])}", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        };
        using var source = Create(handler, secrets, browser);

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.NotEmpty(collections);
        Assert.NotNull(opened);
        Assert.Equal("login.microsoftonline.com", opened.Host);
        Assert.StartsWith("/consumers/", opened.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", opened.Query, StringComparison.Ordinal);
        Assert.Contains("Calendars.Read", Uri.UnescapeDataString(opened.Query), StringComparison.Ordinal);
        Assert.Contains("Tasks.Read", Uri.UnescapeDataString(opened.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("ReadWrite", Uri.UnescapeDataString(opened.Query), StringComparison.Ordinal);
        Assert.Equal("refresh_1", await secrets.GetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, Ct));

        var exchange = handler.Requests.Single(r => r.Request.RequestUri!.Host == "login.microsoftonline.com").Body;
        Assert.Equal("authorization_code", Field(exchange, "grant_type"));
        Assert.Equal("abc123", Field(exchange, "code"));
        Assert.Equal(43 + 21, Field(exchange, "code_verifier").Length);
        Assert.DoesNotContain("client_secret", exchange, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_with_the_wrong_state_is_ignored_and_the_right_one_completes_sign_in()
    {
        var secrets = new InMemorySecretStore();
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("refresh_1")));
        BrowserLauncher browser = async (address, ct) =>
        {
            var query = address.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            using var http = new HttpClient();
            using var forged = await http.GetAsync($"{query["redirect_uri"]}?code=forged&state=wrong", ct);
            Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
            using var real = await http.GetAsync($"{query["redirect_uri"]}?code=real&state={Uri.EscapeDataString(query["state"])}", ct);
        };
        using var source = Create(handler, secrets, browser);

        await source.GetCollectionsAsync(Ct);

        Assert.Equal("real", Field(handler.Requests.Single(r => r.Request.RequestUri!.Host == "login.microsoftonline.com").Body, "code"));
    }

    [Fact]
    public async Task Declining_in_the_browser_is_an_auth_error_and_nothing_is_stored()
    {
        var secrets = new InMemorySecretStore();
        BrowserLauncher browser = async (address, ct) =>
        {
            var query = address.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            using var http = new HttpClient();
            using var response = await http.GetAsync($"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"])}", ct);
        };
        using var source = Create(Handler(_ => FakeHandler.Json("{}")), secrets, browser);

        await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Null(await secrets.GetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, Ct));
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
    public async Task A_refresh_stores_the_rotated_token_and_the_access_token_is_reused_until_it_nears_expiry()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, "old", Ct);
        var handler = Handler(_ => FakeHandler.Json(Setup.Token("rotated")));
        using var source = Create(handler, secrets, (_, _) => throw new InvalidOperationException());

        await source.GetCollectionsAsync(Ct);
        await source.GetCollectionsAsync(Ct);

        Assert.Equal("rotated", await secrets.GetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, Ct));
        var tokenCalls = handler.Requests.Where(r => r.Request.RequestUri!.Host == "login.microsoftonline.com").ToList();
        Assert.Single(tokenCalls);
        Assert.Equal("refresh_token", Field(tokenCalls[0].Body, "grant_type"));
        Assert.Equal("old", Field(tokenCalls[0].Body, "refresh_token"));
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_deleted_and_asks_the_user_to_sign_in_again_without_leaking_it()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, Setup.RefreshToken, Ct);
        var handler = Handler(_ => FakeHandler.Json("""{"error":"invalid_grant","error_description":"AADSTS70008: user@example.com"}""", HttpStatusCode.BadRequest));
        using var source = Create(handler, secrets, (_, _) => throw new InvalidOperationException());

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Null(await secrets.GetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, Ct));
        var text = ex.Message + ex.Diagnostics;
        Assert.DoesNotContain(Setup.RefreshToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_client_id_the_source_says_how_to_get_one_and_does_not_open_a_browser()
    {
        using var source = Create(Handler(_ => FakeHandler.Json("{}")), new InMemorySecretStore(), (_, _) => throw new InvalidOperationException("opened"), clientId: null);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Contains("client", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_client_id_set_on_the_source_wins_over_the_built_in_one_and_a_malformed_one_is_refused()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(Setup.SourceId, MicrosoftAuth.RefreshTokenField, "r", Ct);
        var handler = Handler(_ => FakeHandler.Json(Setup.Token()));
        using (var own = Create(handler, secrets, (_, _) => Task.CompletedTask, ownId: "99999999-0000-0000-0000-000000000001"))
        {
            await own.GetCollectionsAsync(Ct);
            Assert.Equal("99999999-0000-0000-0000-000000000001", Field(handler.Requests[0].Body, "client_id"));
        }

        using var bad = Create(handler, secrets, (_, _) => Task.CompletedTask, ownId: "not-a-guid");
        await Assert.ThrowsAsync<SourceAuthException>(() => bad.GetCollectionsAsync(Ct));
    }

    [Fact]
    public async Task Tokens_never_appear_in_an_error_from_graph()
    {
        var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), calendar: false);

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        var text = ex + ex.Diagnostics;
        Assert.DoesNotContain(Setup.AccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Setup.RefreshToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bearer_token_goes_only_to_graph()
    {
        var (handler, source, _, _) = Setup.Create(_ => Setup.Page("""{"id":"l","displayName":"Tasks"}"""), calendar: false);

        await source.GetCollectionsAsync(Ct);

        Assert.All(handler.GraphRequests, r => Assert.Equal(Setup.AccessToken, r.Headers.Authorization?.Parameter));
        Assert.All(handler.Requests.Where(r => r.Request.RequestUri!.Host != "graph.microsoft.com"), r => Assert.Null(r.Request.Headers.Authorization));
    }

    [Fact]
    public void The_schema_offers_a_client_id_both_features_and_the_refresh_interval()
    {
        var factory = new MicrosoftFactory(new FakeHandler());

        Assert.Equal("microsoft", factory.Kind);
        Assert.Equal([MicrosoftFactory.ClientIdField, MicrosoftFactory.CalendarField, MicrosoftFactory.TodoField, "refreshMinutes"], factory.ConfigSchema.Fields.Select(f => f.Key));
    }

    [Fact]
    public void The_client_id_is_required_without_a_built_in_one_and_must_be_a_guid()
    {
        var without = new MicrosoftFactory(new FakeHandler());
        Assert.True(without.ConfigSchema.Fields.Single(f => f.Key == MicrosoftFactory.ClientIdField).Required);

        var with = new MicrosoftFactory(new FakeHandler(), defaultClientId: "11111111-2222-3333-4444-555555555555");
        Assert.False(with.ConfigSchema.Fields.Single(f => f.Key == MicrosoftFactory.ClientIdField).Required);

        Assert.Empty(without.ValidateValues(new Dictionary<string, string> { [MicrosoftFactory.ClientIdField] = "11111111-2222-3333-4444-555555555555" }));
        Assert.Empty(without.ValidateValues(new Dictionary<string, string>()));
        Assert.Single(without.ValidateValues(new Dictionary<string, string> { [MicrosoftFactory.ClientIdField] = "consumers" }));
    }
}
