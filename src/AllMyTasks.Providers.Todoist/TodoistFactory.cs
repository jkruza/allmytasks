using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Todoist;

/// <summary>Creates <c>todoist</c> sources from a personal API token, which is kept in the secret store only.</summary>
public sealed class TodoistFactory(HttpMessageHandler? handler = null, ILogger? logger = null, TimeZoneInfo? displayZone = null) : ISourceFactory, IDisposable
{
    public const string TokenField = "token";
    public const string AllowChangesField = "allowChanges";

    private readonly HttpMessageHandler _handler = handler ?? TodoistClient.CreateDefaultHandler();
    private readonly bool _ownsHandler = handler is null;

    public string Kind => TodoistSource.KindName;

    public string DisplayName => "Todoist";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            TokenField,
            "API token",
            FieldKind.Password,
            Required: true,
            Help: "In Todoist open Settings, Integrations, Developer and copy the API token. It gives full access to your account, so the app only reads with it unless you allow changes below. It is stored in your system keyring."),
        new SourceConfigField(
            AllowChangesField,
            "Allow All My Tasks to change tasks in Todoist (preview)",
            FieldKind.Bool,
            Help: "Preview of a version 2 feature: lets you complete and reopen tasks from the app. Off by default: the app then only reads."),
        new SourceConfigField(SyncScheduler.IntervalKey, "Refresh every (minutes)", FieldKind.Number, Help: "Leave blank for 15."),
    ]);

    public void Dispose()
    {
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        var client = new TodoistClient(_handler, async ct => (await secrets.GetAsync(sourceId, TokenField, ct).ConfigureAwait(false))?.Trim());
        var zone = displayZone ?? TimeZoneInfo.Local;
        return string.Equals(config.Get(AllowChangesField), "true", StringComparison.OrdinalIgnoreCase)
            ? new WritableTodoistSource(sourceId, displayName, client, zone, logger)
            : new TodoistSource(sourceId, displayName, client, zone, logger);
    }
}
