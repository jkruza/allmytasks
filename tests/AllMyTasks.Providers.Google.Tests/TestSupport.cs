using System.Net;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Providers.Google.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return Respond(request, body);
    }

    public IEnumerable<HttpRequestMessage> ApiRequests => Requests.Select(r => r.Request).Where(r => r.RequestUri!.Host.EndsWith("googleapis.com", StringComparison.Ordinal) && r.RequestUri.Host != "oauth2.googleapis.com");

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Setup
{
    internal const string ClientId = "client-123.apps.googleusercontent.com";
    internal const string ClientSecret = "client_SECRET_789";
    internal const string RefreshToken = "refresh_SECRET_123";
    internal const string AccessToken = "access_SECRET_456";
    internal const string SourceId = "g-1";

    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A token reply that grants every scope this source asks for.</summary>
    public static string Token(string refresh = RefreshToken, string? scopes = null, string? idToken = null) =>
        $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{refresh}}","expires_in":3600,"scope":"{{scopes ?? $"openid {GoogleAuth.CalendarScope} {GoogleAuth.TasksScope}"}}"{{(idToken is null ? "" : $",\"id_token\":\"{idToken}\"")}}}""";

    public static string IdToken(string sub) =>
        $"e30.{Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"sub":"{{sub}}"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.sig";

    /// <summary>A source that is already signed in; API requests are answered by <paramref name="api"/>.</summary>
    public static (FakeHandler Handler, GoogleSource Source, InMemorySecretStore Secrets, ManualTime Time) Create(
        Func<HttpRequestMessage, HttpResponseMessage> api,
        bool calendar = true,
        bool tasks = true)
    {
        var handler = new FakeHandler();
        handler.Respond = (request, _) => request.RequestUri!.Host is "oauth2.googleapis.com" ? FakeHandler.Json(Token()) : api(request);
        var secrets = new InMemorySecretStore();
        secrets.SetAsync(SourceId, GoogleAuth.RefreshTokenField, RefreshToken).GetAwaiter().GetResult();
        var time = new ManualTime(Now);
        var factory = new GoogleFactory(handler, defaultClientId: ClientId, defaultClientSecret: ClientSecret);
        var config = new SourceConfig(new Dictionary<string, string>
        {
            [GoogleFactory.CalendarField] = calendar && !tasks ? "true" : string.Empty,
            [GoogleFactory.TasksField] = tasks && !calendar ? "true" : string.Empty,
        });
        var source = factory.Create(SourceId, "Google", config, secrets, (_, _) => throw new InvalidOperationException("No browser expected."), time);
        return (handler, source, secrets, time);
    }

    public static CollectionInfo Calendar(string id = "cal1") => new(SourceId, id, "Calendar", CollectionKind.Calendar);

    public static CollectionInfo List(string id = "list1") => new(SourceId, id, "Tasks", CollectionKind.TaskList);

    public static HttpResponseMessage Events(string items, string? next = null, string? sync = null) =>
        FakeHandler.Json($$"""{"kind":"calendar#events","items":[{{items}}]{{(next is null ? "" : $",\"nextPageToken\":\"{next}\"")}}{{(sync is null ? "" : $",\"nextSyncToken\":\"{sync}\"")}}}""");

    public static HttpResponseMessage Tasks(string items, string? next = null) =>
        FakeHandler.Json($$"""{"kind":"tasks#tasks"{{(items.Length == 0 ? "" : $",\"items\":[{items}]")}}{{(next is null ? "" : $",\"nextPageToken\":\"{next}\"")}}}""");

    public static HttpResponseMessage Page(string items, string? next = null) =>
        FakeHandler.Json($$"""{"items":[{{items}}]{{(next is null ? "" : $",\"nextPageToken\":\"{next}\"")}}}""");

    public static string Query(HttpRequestMessage request, string name) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[name] ?? string.Empty;
}
