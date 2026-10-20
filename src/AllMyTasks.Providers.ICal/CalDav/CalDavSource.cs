using System.Xml.Linq;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using Microsoft.Extensions.Logging;
using static AllMyTasks.Providers.ICal.CalDav.DavXml;

namespace AllMyTasks.Providers.ICal.CalDav;

/// <summary>A calendar found on the server.</summary>
internal sealed record DavCalendar(string Href, string Name, string? Color, bool SupportsSyncCollection, bool HasSyncToken);

/// <summary>
/// A CalDAV account (kind <c>caldav</c>): discovers calendars from a server address and syncs their events, read-only.
/// Strategy per collection, best first: <c>sync-collection</c> (RFC 6578), then ctag + etag diff, then a full
/// <c>calendar-query</c>. Calendar ids are the collection paths.
/// </summary>
public sealed class CalDavSource : SourceBase, ICalendarSource, IDisposable
{
    public const string KindName = "caldav";
    internal const int MultigetBatch = 50;

    private readonly CalDavClient _client;
    private readonly Uri _baseUrl;
    private readonly Dictionary<string, DavCalendar> _calendars = [];

    internal CalDavSource(
        string id,
        string displayName,
        Uri baseUrl,
        CalDavClient client,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _baseUrl = baseUrl;
        _client = client;
    }

    public void Dispose() => _client.Dispose();

    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var found = await RetryAsync(DiscoverAsync, cancellationToken: cancellationToken).ConfigureAwait(false);
        _calendars.Clear();
        foreach (var calendar in found)
        {
            _calendars[calendar.Href] = calendar;
        }

        return [.. found.Select(c => new CollectionInfo(Id, c.Href, c.Name, CollectionKind.Calendar, c.Color, IsVisible: true, IsReadOnly: true))];
    }

    public async Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!_calendars.TryGetValue(collection.Id, out var calendar))
        {
            // Not discovered in this run (for example the cache was cleared): discover again.
            await GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
            if (!_calendars.TryGetValue(collection.Id, out calendar))
            {
                return SyncResult.Delta<CalendarEvent>([], [], previous ?? new SyncState("{\"Mode\":\"query\",\"Items\":{}}"), [new SyncWarning(null, "This calendar was not found on the server; saved events were kept.")]);
            }
        }

        var state = CalDavState.Parse(previous);
        var url = new Uri(_baseUrl, collection.Id);
        return await RetryAsync(
            ct => SyncCoreAsync(collection, calendar, url, state, previous is null, ct),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<SyncResult<CalendarEvent>> SyncCoreAsync(
        CollectionInfo collection,
        DavCalendar calendar,
        Uri url,
        CalDavState? state,
        bool isFirst,
        CancellationToken cancellationToken)
    {
        if (calendar.SupportsSyncCollection && (state is null || state.Mode == "sync"))
        {
            var viaToken = await TrySyncCollectionAsync(collection, url, state, isFirst, cancellationToken).ConfigureAwait(false);
            if (viaToken is not null)
            {
                return viaToken;
            }
        }

        if (calendar.HasSyncToken || state?.Mode == "ctag" || state is null)
        {
            var ctag = await GetCTagAsync(url, cancellationToken).ConfigureAwait(false);
            if (ctag is not null)
            {
                return await SyncByCTagAsync(collection, url, state?.Mode == "ctag" ? state : null, ctag, isFirst, cancellationToken).ConfigureAwait(false);
            }
        }

        return await SyncByQueryAsync(collection, url, state?.Mode == "query" ? state : null, isFirst, cancellationToken).ConfigureAwait(false);
    }

    // ---- Strategy 1: sync-collection (RFC 6578) -------------------------------------------------

    private async Task<SyncResult<CalendarEvent>?> TrySyncCollectionAsync(
        CollectionInfo collection,
        Uri url,
        CalDavState? state,
        bool isFirst,
        CancellationToken cancellationToken)
    {
        var reply = await _client.ReportAsync(url, SyncCollectionBody(state?.Token ?? string.Empty), cancellationToken).ConfigureAwait(false);
        if (reply.Status is 404)
        {
            return NotFound(state);
        }

        if (reply.Status is 403 or 409 && reply.Body.Contains("valid-sync-token", StringComparison.Ordinal))
        {
            if (state is not null)
            {
                throw new SyncStateExpiredException("The server no longer accepts the saved sync token.");
            }

            return null;
        }

        if (reply.Status is not (207 or 200))
        {
            // 501, 403 without the precondition, 415 ...: the server does not really support it. Use another strategy.
            return null;
        }

        var multistatus = ParseMultistatus(reply.Body);
        if (multistatus.SyncToken is null)
        {
            return null;
        }

        var items = new Dictionary<string, ResourceRef>(state?.Items ?? []);
        var deleted = new List<string>();
        var changed = new List<string>();
        var collectionPath = new Uri(_baseUrl, collection.Id).AbsolutePath.TrimEnd('/');
        foreach (var response in multistatus.Responses)
        {
            var href = PathOf(url, response.Href);
            if (href.TrimEnd('/') == collectionPath)
            {
                continue;
            }

            if (response.IsGone)
            {
                if (items.Remove(href, out var old))
                {
                    deleted.AddRange(old.Uids);
                }
            }
            else
            {
                changed.Add(href);
            }
        }

        var (upserts, warnings) = await FetchAsync(collection, url, changed, items, deleted, cancellationToken).ConfigureAwait(false);
        var newState = new CalDavState("sync", multistatus.SyncToken, null, items);
        return Build(isFirst, upserts, deleted, newState, warnings);
    }

    // ---- Strategy 2: ctag + etag diff -----------------------------------------------------------

    private async Task<string?> GetCTagAsync(Uri url, CancellationToken cancellationToken)
    {
        var reply = await _client.PropfindAsync(url, 0, PropfindBody(Cs + "getctag", DavXml.D + "sync-token"), cancellationToken).ConfigureAwait(false);
        if (reply.Status is not (207 or 200))
        {
            return null;
        }

        var response = ParseMultistatus(reply.Body).First;
        return response?.Text(Cs + "getctag") ?? response?.Text(DavXml.D + "sync-token");
    }

    private async Task<SyncResult<CalendarEvent>> SyncByCTagAsync(
        CollectionInfo collection,
        Uri url,
        CalDavState? state,
        string ctag,
        bool isFirst,
        CancellationToken cancellationToken)
    {
        if (state is not null && state.CTag == ctag)
        {
            return SyncResult.Delta<CalendarEvent>([], [], state.ToSyncState());
        }

        var reply = await _client.PropfindAsync(url, 1, PropfindBody(DavXml.D + "getetag", DavXml.D + "resourcetype"), cancellationToken).ConfigureAwait(false);
        if (reply.Status == 404)
        {
            return NotFound(state);
        }

        if (reply.Status is not (207 or 200))
        {
            throw new SourceException($"The server answered HTTP {reply.Status} when listing the calendar.");
        }

        var listing = ParseMultistatus(reply.Body);
        var (changed, deleted, items) = Diff(url, listing, state?.Items ?? [], collection.Id);
        var (upserts, warnings) = await FetchAsync(collection, url, changed, items, deleted, cancellationToken).ConfigureAwait(false);
        return Build(isFirst, upserts, deleted, new CalDavState("ctag", null, ctag, items), warnings);
    }

    // ---- Strategy 3: full calendar-query --------------------------------------------------------

    private async Task<SyncResult<CalendarEvent>> SyncByQueryAsync(
        CollectionInfo collection,
        Uri url,
        CalDavState? state,
        bool isFirst,
        CancellationToken cancellationToken)
    {
        var reply = await _client.ReportAsync(url, CalendarQueryBody(), cancellationToken).ConfigureAwait(false);
        if (reply.Status == 404)
        {
            return NotFound(state);
        }

        if (reply.Status is not (207 or 200))
        {
            throw new SourceException($"The server answered HTTP {reply.Status} for the calendar query.");
        }

        var warnings = new List<SyncWarning>();
        var events = new List<CalendarEvent>();
        var items = new Dictionary<string, ResourceRef>();
        foreach (var response in ParseMultistatus(reply.Body).Responses.Where(r => !r.IsGone))
        {
            Map(collection, url, response, events, items, warnings);
        }

        // Identical to last time (same resources, same etags): nothing to write.
        var newState = new CalDavState("query", null, null, items);
        if (state is not null && SameEtags(state.Items, items))
        {
            return SyncResult.Delta<CalendarEvent>([], [], newState.ToSyncState());
        }

        return SyncResult.Snapshot(events, newState.ToSyncState(), warnings, confirmedEmpty: true);
    }

    // ---- Shared ---------------------------------------------------------------------------------

    private static SyncResult<CalendarEvent> Build(
        bool isFirst,
        List<CalendarEvent> upserts,
        List<string> deleted,
        CalDavState state,
        List<SyncWarning> warnings) =>
        isFirst
            ? SyncResult.Snapshot(upserts, state.ToSyncState(), warnings, confirmedEmpty: true)
            : SyncResult.Delta(upserts, [.. deleted.Distinct()], state.ToSyncState(), warnings);

    private static SyncResult<CalendarEvent> NotFound(CalDavState? state) =>
        SyncResult.Delta<CalendarEvent>(
            [],
            [],
            (state ?? new CalDavState("query", null, null, [])).ToSyncState(),
            [new SyncWarning(null, "This calendar was not found on the server; saved events were kept.")]);

    private static (List<string> Changed, List<string> Deleted, Dictionary<string, ResourceRef> Items) Diff(
        Uri baseUrl,
        Multistatus listing,
        Dictionary<string, ResourceRef> known,
        string collectionId)
    {
        var collectionPath = new Uri(baseUrl, collectionId).AbsolutePath.TrimEnd('/');
        var items = new Dictionary<string, ResourceRef>();
        var changed = new List<string>();
        var present = new HashSet<string>();
        foreach (var response in listing.Responses)
        {
            var href = PathOf(baseUrl, response.Href);
            var etag = response.Text(DavXml.D + "getetag");
            var isCollection = response.Prop(DavXml.D + "resourcetype")?.Element(DavXml.D + "collection") is not null;
            if (href.TrimEnd('/') == collectionPath || isCollection || etag is null || response.IsGone)
            {
                continue;
            }

            present.Add(href);
            if (known.TryGetValue(href, out var existing) && existing.ETag == etag)
            {
                items[href] = existing;
            }
            else
            {
                changed.Add(href);
                items[href] = new ResourceRef(etag, []);
            }
        }

        var deleted = known.Where(k => !present.Contains(k.Key)).SelectMany(k => k.Value.Uids).ToList();
        return (changed, deleted, items);
    }

    /// <summary>Fetches the given resources with <c>calendar-multiget</c> and maps them; updates <paramref name="items"/>.</summary>
    private async Task<(List<CalendarEvent> Upserts, List<SyncWarning> Warnings)> FetchAsync(
        CollectionInfo collection,
        Uri url,
        List<string> hrefs,
        Dictionary<string, ResourceRef> items,
        List<string> deleted,
        CancellationToken cancellationToken)
    {
        var events = new List<CalendarEvent>();
        var warnings = new List<SyncWarning>();
        var previousUids = hrefs.ToDictionary(h => h, h => items.TryGetValue(h, out var r) ? r.Uids : []);
        foreach (var batch in hrefs.Chunk(MultigetBatch))
        {
            var reply = await _client.ReportAsync(url, MultigetBody(batch), cancellationToken).ConfigureAwait(false);
            if (reply.Status is not (207 or 200))
            {
                throw new SourceException($"The server answered HTTP {reply.Status} when fetching events.");
            }

            foreach (var response in ParseMultistatus(reply.Body).Responses.Where(r => !r.IsGone))
            {
                Map(collection, url, response, events, items, warnings);
            }
        }

        // A resource that now holds different UIDs than before no longer holds the old ones.
        foreach (var (href, old) in previousUids)
        {
            if (items.TryGetValue(href, out var now))
            {
                deleted.AddRange(old.Except(now.Uids));
            }
        }

        return (events, warnings);
    }

    private static void Map(
        CollectionInfo collection,
        Uri baseUrl,
        DavResponse response,
        List<CalendarEvent> events,
        Dictionary<string, ResourceRef> items,
        List<SyncWarning> warnings)
    {
        var data = response.Text(C + "calendar-data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var href = PathOf(baseUrl, response.Href);
        try
        {
            var parsed = IcalMapper.Parse(data, collection.SourceId, collection.Id, idSeed: href);
            events.AddRange(parsed.Events);
            warnings.AddRange(parsed.Warnings);
            items[href] = new ResourceRef(response.Text(DavXml.D + "getetag") ?? string.Empty, [.. parsed.Events.Select(e => e.Key.ItemId).Distinct()]);
        }
        catch (SourceDataException)
        {
            warnings.Add(new SyncWarning(null, "A calendar resource could not be read and was skipped."));
        }
    }

    private static bool SameEtags(Dictionary<string, ResourceRef> a, Dictionary<string, ResourceRef> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.ETag == kv.Value.ETag);

    /// <summary>The path of an href, which servers write as an absolute path or a full URL.</summary>
    internal static string PathOf(Uri baseUrl, string href) => new Uri(baseUrl, href).AbsolutePath;

    // ---- Discovery (RFC 6764 / RFC 4791) --------------------------------------------------------

    private async Task<List<DavCalendar>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<Uri> { _baseUrl };
        if (_baseUrl.AbsolutePath is "" or "/")
        {
            candidates.Add(new Uri(_baseUrl, "/.well-known/caldav"));
        }

        SourceException? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                if (await DiscoverFromAsync(candidate, cancellationToken).ConfigureAwait(false) is { } calendars)
                {
                    return calendars;
                }
            }
            catch (SourceException ex) when (ex is not SourceAuthException and not SourceUnavailableException && candidate != candidates[^1])
            {
                last = ex;
            }
        }

        throw last ?? new SourceDataException("No calendars were found at this address.");
    }

    private async Task<List<DavCalendar>?> DiscoverFromAsync(Uri url, CancellationToken cancellationToken)
    {
        var props = PropfindBody(
            DavXml.D + "resourcetype",
            DavXml.D + "displayname",
            DavXml.D + "current-user-principal",
            C + "calendar-home-set",
            C + "supported-calendar-component-set",
            Apple + "calendar-color",
            Cs + "getctag",
            DavXml.D + "sync-token",
            DavXml.D + "supported-report-set");
        var reply = await _client.PropfindAsync(url, 0, props, cancellationToken).ConfigureAwait(false);
        if (reply.Status is 404 or 405 or 403)
        {
            return null;
        }

        if (reply.Status is not (207 or 200))
        {
            throw new SourceException($"The server answered HTTP {reply.Status} while looking for calendars.");
        }

        var first = ParseMultistatus(reply.Body).First
            ?? throw new SourceDataException("The server returned no information about this address.");
        var finalUrl = reply.FinalUrl;

        // The address may be a calendar itself.
        if (IsCalendar(first))
        {
            var self = ToCalendar(first, finalUrl.AbsolutePath);
            return self is null ? [] : [self];
        }

        var home = first.Prop(C + "calendar-home-set")?.Element(DavXml.D + "href")?.Value;
        if (home is null)
        {
            var principal = first.Prop(DavXml.D + "current-user-principal")?.Element(DavXml.D + "href")?.Value;
            if (principal is null)
            {
                return null;
            }

            var principalReply = await _client.PropfindAsync(new Uri(finalUrl, principal), 0, PropfindBody(C + "calendar-home-set"), cancellationToken).ConfigureAwait(false);
            home = ParseMultistatus(principalReply.Body).First?.Prop(C + "calendar-home-set")?.Element(DavXml.D + "href")?.Value;
            if (home is null)
            {
                return null;
            }
        }

        var homeUrl = new Uri(finalUrl, home);
        var listing = await _client.PropfindAsync(homeUrl, 1, props, cancellationToken).ConfigureAwait(false);
        if (listing.Status is not (207 or 200))
        {
            throw new SourceException($"The server answered HTTP {listing.Status} while listing calendars.");
        }

        var result = new List<DavCalendar>();
        foreach (var response in ParseMultistatus(listing.Body).Responses)
        {
            if (!IsCalendar(response))
            {
                continue;
            }

            if (ToCalendar(response, PathOf(homeUrl, response.Href)) is { } calendar)
            {
                result.Add(calendar);
            }
        }

        return result;
    }

    private static bool IsCalendar(DavResponse response) =>
        response.Prop(DavXml.D + "resourcetype")?.Element(C + "calendar") is not null;

    private static DavCalendar? ToCalendar(DavResponse response, string href)
    {
        // Only calendars that can hold events (when the server says what they hold).
        var components = response.Prop(C + "supported-calendar-component-set");
        if (components is not null && !components.Elements(C + "comp").Any(c => string.Equals((string?)c.Attribute("name"), "VEVENT", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var reports = response.Prop(DavXml.D + "supported-report-set");
        var supportsSync = reports?.Descendants(DavXml.D + "sync-collection").Any() == true;
        var hasToken = response.Text(DavXml.D + "sync-token") is { Length: > 0 };
        var name = response.Text(DavXml.D + "displayname") is { Length: > 0 } n ? n : LastSegment(href);
        return new DavCalendar(href, name, NormalizeColor(response.Text(Apple + "calendar-color")), supportsSync || hasToken, hasToken);
    }

    private static string LastSegment(string href)
    {
        var parts = href.TrimEnd('/').Split('/');
        return Uri.UnescapeDataString(parts[^1]);
    }

    /// <summary>Apple servers send <c>#RRGGBBAA</c>; the alpha is dropped. Anything else that is not a hex colour is ignored.</summary>
    internal static string? NormalizeColor(string? color)
    {
        var hex = color?.Trim().TrimStart('#');
        return hex is { Length: 6 or 8 } && hex.All(Uri.IsHexDigit) ? "#" + hex[..6].ToLowerInvariant() : null;
    }
}
