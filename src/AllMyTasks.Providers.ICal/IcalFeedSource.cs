using System.Text.Json;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.ICal;

/// <summary>
/// A single <c>.ics</c> feed (kind <c>ical</c>): one read-only calendar. Each sync is a conditional GET; a changed
/// body is parsed into a full snapshot. The feed address is a credential, so it is read from the secret store at
/// sync time through <paramref name="getUrl"/> and never logged beyond scheme and host.
/// </summary>
public sealed class IcalFeedSource : SourceBase, ICalendarSource
{
    public const string KindName = "ical";
    public const string CollectionId = "feed";

    private readonly IcalFeedClient _client;
    private readonly Func<CancellationToken, Task<string?>> _getUrl;
    private string? _calendarName;

    public IcalFeedSource(
        string id,
        string displayName,
        IcalFeedClient client,
        Func<CancellationToken, Task<string?>> getUrl,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _getUrl = getUrl;
    }

    public override Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollectionInfo>>(
        [
            new CollectionInfo(Id, CollectionId, string.IsNullOrWhiteSpace(_calendarName) ? DisplayName : _calendarName, CollectionKind.Calendar, IsReadOnly: true),
        ]);

    public async Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var url = IcalUrl.Normalize(await _getUrl(cancellationToken).ConfigureAwait(false))
            ?? throw new SourceAuthException("The feed address is missing or not valid. Edit the source to enter it again.");

        var known = FeedState.Parse(previous);
        var response = await RetryAsync(
            ct => _client.FetchAsync(url, known is null ? null : new FeedValidators(known.ETag, known.LastModified), ct),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.NotModified && known is not null)
        {
            return SyncResult.Delta<CalendarEvent>([], [], previous!);
        }

        var state = new FeedState(response.ETag, response.LastModified, response.Hash);
        if (known is not null && known.Hash == response.Hash)
        {
            // Same bytes: nothing to parse or write, but keep the newest validators.
            return SyncResult.Delta<CalendarEvent>([], [], state.ToSyncState());
        }

        IcalParseResult parsed;
        try
        {
            parsed = IcalMapper.Parse(response.Text, collection.SourceId, collection.Id);
        }
        catch (SourceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The parser's own messages can quote feed content.
            throw new SourceDataException("The feed could not be read.", ex);
        }

        _calendarName = parsed.CalendarName;
        // A valid calendar with no events is genuinely empty; error pages never get this far (not iCalendar).
        return SyncResult.Snapshot(parsed.Events, state.ToSyncState(), parsed.Warnings, confirmedEmpty: true);
    }

    private sealed record FeedState(string? ETag, string? LastModified, string Hash)
    {
        public static FeedState? Parse(SyncState? state)
        {
            if (state is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<FeedState>(state.Token);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public SyncState ToSyncState() => new(JsonSerializer.Serialize(this));
    }
}

internal static class IcalUrl
{
    public static Uri? Normalize(string? url) => UrlRedactor.NormalizeFeedUrl(url);
}
