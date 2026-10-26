using System.Net;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Providers.Microsoft.Tests;

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

    public IEnumerable<HttpRequestMessage> GraphRequests => Requests.Select(r => r.Request).Where(r => r.RequestUri!.Host == "graph.microsoft.com");

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
    internal const string ClientId = "11111111-2222-3333-4444-555555555555";
    internal const string RefreshToken = "refresh_SECRET_123";
    internal const string AccessToken = "access_SECRET_456";
    internal const string SourceId = "ms-1";

    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static string Token(string refresh = RefreshToken) =>
        $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{refresh}}","expires_in":3600}""";

    /// <summary>A source that is already signed in; graph requests are answered by <paramref name="graph"/>.</summary>
    public static (FakeHandler Handler, MicrosoftSource Source, InMemorySecretStore Secrets, ManualTime Time) Create(
        Func<HttpRequestMessage, HttpResponseMessage> graph,
        bool calendar = true,
        bool todo = true)
    {
        var handler = new FakeHandler();
        handler.Respond = (request, _) => request.RequestUri!.Host == "login.microsoftonline.com" ? FakeHandler.Json(Token()) : graph(request);
        var secrets = new InMemorySecretStore();
        secrets.SetAsync(SourceId, MicrosoftAuth.RefreshTokenField, RefreshToken).GetAwaiter().GetResult();
        var time = new ManualTime(Now);
        var factory = new MicrosoftFactory(handler, displayZone: Warsaw, defaultClientId: ClientId);
        var config = new SourceConfig(new Dictionary<string, string>
        {
            [MicrosoftFactory.CalendarField] = calendar && !todo ? "true" : string.Empty,
            [MicrosoftFactory.TodoField] = todo && !calendar ? "true" : string.Empty,
        });
        var source = factory.Create(SourceId, "Microsoft", config, secrets, (_, _) => throw new InvalidOperationException("No browser expected."), time);
        return (handler, source, secrets, time);
    }

    public static CollectionInfo Calendar(string id = "cal1") => new(SourceId, id, "Calendar", CollectionKind.Calendar);

    public static CollectionInfo List(string id = "list1") => new(SourceId, id, "Tasks", CollectionKind.TaskList);

    public static HttpResponseMessage Page(string value, string? next = null, string? delta = null)
    {
        var tail = (next is null ? string.Empty : $",\"@odata.nextLink\":\"{next}\"") + (delta is null ? string.Empty : $",\"@odata.deltaLink\":\"{delta}\"");
        return FakeHandler.Json($$"""{"value":[{{value}}]{{tail}}}""");
    }
}
