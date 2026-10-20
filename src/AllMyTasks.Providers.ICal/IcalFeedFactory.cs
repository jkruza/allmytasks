using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.ICal;

/// <summary>Creates <c>ical</c> sources from a feed address.</summary>
public sealed class IcalFeedFactory(HttpMessageHandler? handler = null, ILogger? logger = null) : ISourceFactory, IDisposable
{
    public const string UrlField = "url";

    private readonly IcalFeedClient _client = new(handler ?? IcalFeedClient.CreateDefaultHandler());

    public void Dispose() => _client.Dispose();

    public string Kind => IcalFeedSource.KindName;

    public string DisplayName => "Calendar feed (.ics link)";

    public SourceConfigSchema ConfigSchema { get; } = new(
    [
        new SourceConfigField(
            UrlField,
            "Feed address",
            FieldKind.Url,
            Required: true,
            IsSecret: true,
            Help: "A public or private .ics link (webcal:// works too). Private links often contain a secret, so it is stored like a password."),
        new SourceConfigField(
            SyncScheduler.IntervalKey,
            "Refresh every (minutes)",
            FieldKind.Number,
            Help: "Leave blank for 15."),
    ]);

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return new IcalFeedSource(sourceId, displayName, _client, ct => secrets.GetAsync(sourceId, UrlField, ct), logger);
    }
}
