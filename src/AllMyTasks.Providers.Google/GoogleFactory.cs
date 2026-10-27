using System.Reflection;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Google;

/// <summary>
/// Creates <c>google</c> sources: one Google account with Google Calendar, Google Tasks, or both. Sign-in happens in the browser
/// on the first sync; only the refresh token is kept, in the secret store.
/// </summary>
public sealed class GoogleFactory(
    HttpMessageHandler? handler = null,
    ILogger? logger = null,
    string? defaultClientId = null,
    string? defaultClientSecret = null) : ISourceFactory, IDisposable
{
    public const string ClientIdField = "clientId";
    public const string ClientSecretField = "clientSecret";
    public const string CalendarField = "calendar";
    public const string TasksField = "tasks";

    private readonly HttpMessageHandler _handler = handler ?? GoogleClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;
    private readonly string? _defaultClientId = defaultClientId ?? BuiltIn("GoogleClientId");
    private readonly string? _defaultClientSecret = defaultClientSecret ?? BuiltIn("GoogleClientSecret");

    public string Kind => GoogleSource.KindName;

    public string DisplayName => "Google (Calendar, Tasks)";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            ClientIdField,
            "OAuth client ID",
            FieldKind.Text,
            Help: "Only needed if this build of the app has none built in. Create a \"Desktop app\" OAuth client in your own Google Cloud project, enable the Calendar and Tasks APIs, and paste its client ID. See the docs."),
        new SourceConfigField(
            ClientSecretField,
            "OAuth client secret",
            FieldKind.Password,
            Help: "The secret of the same client. Google does not treat a desktop client's secret as confidential; it is stored in your system keyring anyway."),
        new SourceConfigField(CalendarField, "Google Calendar", FieldKind.Bool, Help: "Leave both switches off to read both calendars and tasks."),
        new SourceConfigField(TasksField, "Google Tasks", FieldKind.Bool),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15. After you save, a browser window opens to sign in to your Google account."),
    ]);

    public void Dispose()
    {
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets)
        => Create(sourceId, displayName, config, secrets, GoogleAuth.OpenWithShellAsync);

    internal GoogleSource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets, BrowserLauncher openBrowser, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        var calendar = IsOn(config.Get(CalendarField));
        var tasks = IsOn(config.Get(TasksField));
        if (!calendar && !tasks)
        {
            calendar = tasks = true;
        }

        string[] scopes = [.. calendar ? new[] { GoogleAuth.CalendarScope } : [], .. tasks ? new[] { GoogleAuth.TasksScope } : []];
        var ownId = config.Get(ClientIdField) is { Length: > 0 } own ? own.Trim() : null;

        // A client id of the user's own comes with the user's own secret; the built-in secret belongs to the built-in id.
        async Task<string?> ClientSecret(CancellationToken ct) =>
            ownId is null ? _defaultClientSecret : await secrets.GetAsync(sourceId, ClientSecretField, ct).ConfigureAwait(false);

        var auth = new GoogleAuth(_handler, sourceId, secrets, ownId ?? _defaultClientId, ClientSecret, scopes, openBrowser, time);
        var client = new GoogleClient(_handler, auth.GetAccessTokenAsync, auth.Invalidate);
        return new GoogleSource(sourceId, displayName, client, auth, calendar, tasks, logger, time);
    }

    private static bool IsOn(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? BuiltIn(string key) =>
        typeof(GoogleFactory).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
