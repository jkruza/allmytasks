using System.Reflection;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>
/// Creates <c>microsoft</c> sources: one personal Microsoft account with Outlook calendars, To Do, or both. Sign-in happens in
/// the browser on the first sync; only the refresh token is kept, in the secret store.
/// </summary>
public sealed class MicrosoftFactory(
    HttpMessageHandler? handler = null,
    ILogger? logger = null,
    TimeZoneInfo? displayZone = null,
    string? defaultClientId = null) : ISourceFactory, IDisposable
{
    public const string ClientIdField = "clientId";
    public const string CalendarField = "calendar";
    public const string TodoField = "todo";

    private readonly HttpMessageHandler _handler = handler ?? GraphClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;
    private readonly string? _defaultClientId = defaultClientId ?? BuiltInClientId();

    public string Kind => MicrosoftSource.KindName;

    public string DisplayName => "Microsoft (Outlook calendar, To Do)";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            ClientIdField,
            "Application (client) ID",
            FieldKind.Text,
            Help: "Only needed if this build of the app has none built in. Register a free app in the Microsoft Entra admin center for personal accounts, with a \"Mobile and desktop\" redirect http://localhost, and paste its Application (client) ID. See the docs."),
        new SourceConfigField(CalendarField, "Outlook calendar", FieldKind.Bool, Help: "Leave both switches off to read both calendar and tasks."),
        new SourceConfigField(TodoField, "Microsoft To Do", FieldKind.Bool),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15. After you save, a browser window opens to sign in to your Microsoft account."),
    ]);

    public void Dispose()
    {
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets)
        => Create(sourceId, displayName, config, secrets, MicrosoftAuth.OpenWithShellAsync);

    internal MicrosoftSource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets, BrowserLauncher openBrowser, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        var calendar = IsOn(config.Get(CalendarField));
        var todo = IsOn(config.Get(TodoField));
        if (!calendar && !todo)
        {
            calendar = todo = true;
        }

        string[] scopes = ["User.Read", .. calendar ? new[] { "Calendars.Read" } : [], .. todo ? new[] { "Tasks.Read" } : []];
        var clientId = config.Get(ClientIdField) is { Length: > 0 } own ? own.Trim() : _defaultClientId;
        var auth = new MicrosoftAuth(_handler, sourceId, secrets, clientId, scopes, openBrowser, time);
        var graph = new GraphClient(_handler, auth.GetAccessTokenAsync);
        return new MicrosoftSource(sourceId, displayName, graph, auth, calendar, todo, displayZone ?? TimeZoneInfo.Local, logger, time);
    }

    private static bool IsOn(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? BuiltInClientId() =>
        typeof(MicrosoftFactory).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "MicrosoftClientId")?.Value;
}
