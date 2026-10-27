using System.Globalization;
using System.Text.Json;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>
/// A personal Microsoft account (kind <c>microsoft</c>): Outlook calendars and Microsoft To Do lists through Microsoft Graph,
/// read-only. Calendars sync with <c>calendarView/delta</c> over a rolling window and are stored as expanded occurrences; task
/// lists sync with <c>tasks/delta</c>.
/// </summary>
public sealed class MicrosoftSource : SourceBase, ICalendarSource, ITaskSource, IDisposable
{
    public const string KindName = "microsoft";

    internal const int MaxPages = 500;
    internal static readonly TimeSpan SnapshotInterval = TimeSpan.FromHours(24);

    internal const string CalendarPrefer = "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\", IdType=\"ImmutableId\", odata.maxpagesize=100";
    internal const string TodoPrefer = "odata.maxpagesize=100";

    private readonly GraphClient _graph;
    private readonly MicrosoftAuth _auth;
    private readonly bool _calendars;
    private readonly bool _todo;
    private readonly CalendarMapper _calendarMapper;
    private readonly TodoMapper _todoMapper;

    internal MicrosoftSource(
        string id,
        string displayName,
        GraphClient graph,
        MicrosoftAuth auth,
        bool calendars,
        bool todo,
        TimeZoneInfo displayZone,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _graph = graph;
        _auth = auth;
        _calendars = calendars;
        _todo = todo;
        _calendarMapper = new CalendarMapper(id);
        _todoMapper = new TodoMapper(id, displayZone);
    }

    public void Dispose()
    {
        _graph.Dispose();
        _auth.Dispose();
    }

    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CollectionInfo>();
        if (_calendars)
        {
            var calendars = await ReadAllAsync(GraphClient.Api("me/calendars"), MicrosoftJson.Default.GraphPageCalendarDto, null, cancellationToken).ConfigureAwait(false);
            result.AddRange(calendars.Select(_calendarMapper.MapCalendar).OfType<CollectionInfo>());
        }

        if (_todo)
        {
            var lists = await ReadAllAsync(GraphClient.Api("me/todo/lists"), MicrosoftJson.Default.GraphPageTodoListDto, null, cancellationToken).ConfigureAwait(false);

            // The flagged-email list is kept but listed after the others.
            result.AddRange(lists.OrderBy(l => l.WellknownListName == "flaggedEmails").Select(_todoMapper.MapList).OfType<CollectionInfo>());
        }

        return result;
    }

    public async Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var now = TimeProvider.GetUtcNow();
        var state = ParseState(previous, MicrosoftJson.Default.CalendarState);
        var roll = state?.DeltaLink is null || state.End - now < TimeSpan.FromDays(274);
        Uri url;
        CalendarState window;
        if (roll)
        {
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            window = new CalendarState { Start = monthStart.AddMonths(-3), End = monthStart.AddMonths(13) };
            url = GraphClient.Api(
                $"me/calendars/{Uri.EscapeDataString(collection.Id)}/calendarView/delta?startDateTime={Stamp(window.Start)}&endDateTime={Stamp(window.End)}");
        }
        else
        {
            window = state!;
            url = PinSaved(state!.DeltaLink);
        }

        Page<EventDto> read;
        try
        {
            read = await ReadDeltaAsync(url, MicrosoftJson.Default.GraphPageEventDto, CalendarPrefer, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphNotFoundException ex)
        {
            return NotFound<CalendarEvent>(previous, ex);
        }

        var warnings = new List<SyncWarning>();
        var latest = new Dictionary<string, EventDto>();
        foreach (var dto in read.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
        {
            latest[dto.Id!] = dto;
        }

        var upserts = new List<CalendarEvent>();
        var deleted = new List<string>();
        foreach (var (id, dto) in latest)
        {
            if (dto.Removed is not null)
            {
                deleted.Add(id);
            }
            else if (_calendarMapper.MapEvent(collection.Id, dto, warnings) is { } mapped)
            {
                upserts.Add(mapped);
            }
        }

        var newState = new SyncState(JsonSerializer.Serialize(window with { DeltaLink = read.DeltaLink }, MicrosoftJson.Default.CalendarState));
        return roll
            ? SyncResult.Snapshot<CalendarEvent>(upserts, newState, warnings, confirmedEmpty: true)
            : SyncResult.Delta<CalendarEvent>(upserts, deleted, newState, warnings);
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var now = TimeProvider.GetUtcNow();
        var state = ParseState(previous, MicrosoftJson.Default.TodoState);

        // A checklist-only edit may not show up in a task delta, so a full snapshot is taken now and then.
        var snapshot = state?.DeltaLink is null || now - state.SnapshotAt >= SnapshotInterval;
        var url = snapshot
            ? GraphClient.Api($"me/todo/lists/{Uri.EscapeDataString(collection.Id)}/tasks/delta?$expand=checklistItems")
            : PinSaved(state!.DeltaLink);

        Page<TodoTaskDto> read;
        try
        {
            read = await ReadDeltaAsync(url, MicrosoftJson.Default.GraphPageTodoTaskDto, TodoPrefer, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphNotFoundException ex)
        {
            return NotFound<TaskItem>(previous, ex);
        }

        var warnings = new List<SyncWarning>();
        var latest = new Dictionary<string, TodoTaskDto>();
        foreach (var dto in read.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
        {
            latest[dto.Id!] = dto;
        }

        var checklist = snapshot ? [] : new Dictionary<string, List<string>>(state!.Checklist);
        var upserts = new List<TaskItem>();
        var deleted = new List<string>();
        foreach (var (id, dto) in latest)
        {
            var previousItems = checklist.Remove(id, out var old) ? old : [];
            if (dto.Removed is not null)
            {
                deleted.Add(id);
                deleted.AddRange(previousItems.Select(item => TodoMapper.ChecklistId(id, item)));
                continue;
            }

            var mapped = _todoMapper.MapTask(collection.Id, dto, warnings);
            upserts.AddRange(mapped);
            var items = mapped.Skip(1).Select(t => t.Key.ItemId[(id.Length + 1)..]).ToList();
            deleted.AddRange(previousItems.Except(items).Select(item => TodoMapper.ChecklistId(id, item)));
            if (items.Count > 0)
            {
                checklist[id] = items;
            }
        }

        var newState = new SyncState(JsonSerializer.Serialize(
            new TodoState { DeltaLink = read.DeltaLink, SnapshotAt = snapshot ? now : state!.SnapshotAt, Checklist = checklist },
            MicrosoftJson.Default.TodoState));
        return snapshot
            ? SyncResult.Snapshot(upserts, newState, warnings, confirmedEmpty: true)
            : SyncResult.Delta(upserts, [.. deleted.Distinct()], newState, warnings);
    }

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static T? ParseState<T>(SyncState? previous, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (previous is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(previous.Token, typeInfo);
        }
        catch (JsonException)
        {
            throw new SyncStateExpiredException("The saved Microsoft sync position could not be read.");
        }
    }

    /// <summary>A saved delta address that is not on the Graph host is never followed; the engine starts over instead.</summary>
    private static Uri PinSaved(string? address)
    {
        try
        {
            return GraphClient.Pin(address);
        }
        catch (SourceDataException)
        {
            throw new SyncStateExpiredException("The saved Microsoft sync position is not valid.");
        }
    }

    /// <summary>A calendar or list that is gone keeps what is cached and adds a warning.</summary>
    private static SyncResult<T> NotFound<T>(SyncState? previous, GraphNotFoundException ex) =>
        SyncResult.Delta<T>([], [], previous ?? new SyncState("{\"v\":1}"), [new SyncWarning(null, ex.Message)]);

    private sealed record Page<T>(List<T> Items, string DeltaLink);

    /// <summary>Follows <c>nextLink</c> until a page carries a <c>deltaLink</c>. A reply with neither is an error, never an empty result.</summary>
    private async Task<Page<T>> ReadDeltaAsync<T>(Uri first, System.Text.Json.Serialization.Metadata.JsonTypeInfo<GraphPage<T>> typeInfo, string prefer, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var url = first;
        for (var page = 0; page < MaxPages; page++)
        {
            var reply = await RetryAsync(ct => _graph.GetPageAsync(url, typeInfo, prefer, ct), cancellationToken: cancellationToken).ConfigureAwait(false);
            items.AddRange(reply.Value ?? []);
            if (!string.IsNullOrEmpty(reply.NextLink))
            {
                url = GraphClient.Pin(reply.NextLink);
            }
            else if (!string.IsNullOrEmpty(reply.DeltaLink))
            {
                GraphClient.Pin(reply.DeltaLink);
                return new Page<T>(items, reply.DeltaLink);
            }
            else
            {
                throw new SourceDataException("Microsoft Graph ended a sync without a position to continue from.");
            }
        }

        throw new SourceDataException("Microsoft Graph returned more pages than the app reads in one sync.") { Diagnostics = $"Stopped after {MaxPages} pages" };
    }

    private async Task<List<T>> ReadAllAsync<T>(Uri first, System.Text.Json.Serialization.Metadata.JsonTypeInfo<GraphPage<T>> typeInfo, string? prefer, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var url = first;
        for (var page = 0; page < MaxPages; page++)
        {
            var reply = await RetryAsync(ct => _graph.GetPageAsync(url, typeInfo, prefer, ct), cancellationToken: cancellationToken).ConfigureAwait(false);
            items.AddRange(reply.Value ?? []);
            if (string.IsNullOrEmpty(reply.NextLink))
            {
                return items;
            }

            url = GraphClient.Pin(reply.NextLink);
        }

        throw new SourceDataException("Microsoft Graph returned more pages than the app reads in one sync.") { Diagnostics = $"Stopped after {MaxPages} pages" };
    }
}
