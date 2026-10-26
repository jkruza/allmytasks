using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Todoist;

/// <summary>Creates <c>todoist</c> sources from a personal API token, which is kept in the secret store only.</summary>
public sealed class TodoistFactory(HttpMessageHandler? handler = null, ILogger? logger = null, TimeZoneInfo? displayZone = null) : ISourceFactory, IDisposable
{
    public const string TokenField = "token";

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
            Help: "In Todoist open Settings, Integrations, Developer and copy the API token. It gives full access to your account, so the app only reads with it. It is stored in your system keyring."),
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
        return new TodoistSource(sourceId, displayName, client, displayZone ?? TimeZoneInfo.Local, logger);
    }
}
