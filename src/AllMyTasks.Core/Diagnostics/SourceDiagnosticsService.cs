using System.Security.Cryptography;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Diagnostics;

/// <summary>
/// Collects <see cref="SourceDiagnostics"/> from the sync engine and the local store. Settings are read only to reduce
/// an address of a self-hosted server to its host name; nothing else from a source's settings is used.
/// </summary>
public sealed class SourceDiagnosticsService(SyncEngine engine, ISourceRepository sources, IItemStore items)
{
    // Config keys that hold the server address, for the kinds whose host is useful and not a tenant or account name.
    private static readonly Dictionary<string, (string Key, string? Default)> HostFields = new(StringComparer.Ordinal)
    {
        ["gitlab"] = ("baseUrl", "gitlab.com"),
        ["caldav"] = ("url", null),
    };

    public async Task<IReadOnlyList<SourceDiagnostics>> GetAsync(CancellationToken cancellationToken = default)
    {
        var infos = await sources.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        var statuses = (await engine.GetStatusesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.SourceId);
        var collections = await sources.GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SourceDiagnostics>();
        foreach (var info in infos)
        {
            result.Add(await ForAsync(info, statuses.GetValueOrDefault(info.Id) ?? SourceStatus.NewFor(info.Id), collections, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>The same short id the report uses for a source.</summary>
    public static string ShortId(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return "src-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId)))[..6];
    }

    /// <summary>The host name of a server address, or null if the text is not an absolute http or https address.</summary>
    public static string? HostOnly(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.IdnHost : null;

    private async Task<SourceDiagnostics> ForAsync(SourceInfo info, SourceStatus status, IReadOnlyList<CollectionInfo> all, CancellationToken cancellationToken)
    {
        var parts = new List<CollectionDiagnostics>();
        foreach (var collection in all.Where(c => c.SourceId == info.Id))
        {
            var count = await items.CountItemsAsync(info.Id, collection.Id, cancellationToken).ConfigureAwait(false);
            parts.Add(new CollectionDiagnostics(collection.Kind, collection.IsVisible, count));
        }

        return new SourceDiagnostics(
            ShortId(info.Id),
            info.Kind,
            status.Kind,
            status.LastSyncedAt,
            status.ConsecutiveFailures,
            status.NextRetryAt,
            status.SkippedItems,
            engine.GetLastAttempt(info.Id),
            await HostAsync(info, cancellationToken).ConfigureAwait(false),
            parts);
    }

    private async Task<string?> HostAsync(SourceInfo info, CancellationToken cancellationToken)
    {
        if (!HostFields.TryGetValue(info.Kind, out var field))
        {
            return null;
        }

        var config = await sources.GetSourceConfigAsync(info.Id, cancellationToken).ConfigureAwait(false);
        var address = config?.Get(field.Key);
        if (string.IsNullOrWhiteSpace(address))
        {
            return field.Default;
        }

        return HostOnly(address) ?? "unreadable";
    }
}
