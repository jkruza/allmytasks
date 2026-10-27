using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Google;

/// <summary>
/// A Google account (kind <c>google</c>): Google Calendar calendars and Google Tasks lists, read-only. Calendars sync with the
/// Calendar API's sync tokens and keep recurring events as series plus overrides; task lists have no sync token, so they sync
/// with <c>updatedMin</c> and a full snapshot once a week.
/// </summary>
public sealed class GoogleSource : SourceBase, ICalendarSource, ITaskSource, IDisposable
{
    public const string KindName = "google";

    internal const int MaxPages = 500;
    internal static readonly TimeSpan FullSnapshotInterval = TimeSpan.FromDays(7);
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    internal const string CalendarListFields = "kind,nextPageToken,items(id,summary,summaryOverride,backgroundColor,selected,hidden,deleted)";
    internal const string EventFields = "kind,nextPageToken,nextSyncToken,items(id,status,summary,description,location,htmlLink,start,end,recurrence,recurringEventId,originalStartTime,transparency,updated,eventType,attendees(self,responseStatus))";
    internal const string TaskListFields = "kind,nextPageToken,items(id,title)";
    internal const string TaskFields = "kind,nextPageToken,items(id,title,notes,due,status,completed,parent,webViewLink,updated,deleted)";

    private readonly GoogleClient _client;
    private readonly GoogleAuth _auth;
    private readonly bool _calendars;
    private readonly bool _tasks;
    private readonly EventMapper _eventMapper;
    private readonly TaskMapper _taskMapper;

    internal GoogleSource(
        string id,
        string displayName,
        GoogleClient client,
        GoogleAuth auth,
        bool calendars,
        bool tasks,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _auth = auth;
        _calendars = calendars;
        _tasks = tasks;
        _eventMapper = new EventMapper(id);
        _taskMapper = new TaskMapper(id);
    }

    public void Dispose()
    {
        _client.Dispose();
        _auth.Dispose();
    }

    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CollectionInfo>();
        if (_calendars)
        {
            var calendars = await ReadAllAsync(
                pageToken => GoogleClient.Api(GoogleClient.CalendarRoot, "users/me/calendarList", ("maxResults", "250"), ("fields", CalendarListFields), ("pageToken", pageToken)),
                GoogleJson.Default.ListPageCalendarListEntryDto,
                cancellationToken).ConfigureAwait(false);
            result.AddRange(calendars.Select(_eventMapper.MapCalendar).OfType<CollectionInfo>());
        }

        if (_tasks)
        {
            var lists = await ReadAllAsync(
                pageToken => GoogleClient.Api(GoogleClient.TasksRoot, "users/@me/lists", ("maxResults", "100"), ("fields", TaskListFields), ("pageToken", pageToken)),
                GoogleJson.Default.ListPageTaskListDto,
                cancellationToken).ConfigureAwait(false);
            result.AddRange(lists.Select(_taskMapper.MapList).OfType<CollectionInfo>());
        }

        return result;
    }

    public async Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var state = ParseState(previous, GoogleJson.Default.CalendarState);
        if (state is not null && string.IsNullOrEmpty(state.SyncToken))
        {
            throw new SyncStateExpiredException("The saved Google sync position is not valid.");
        }

        var path = $"calendars/{Uri.EscapeDataString(collection.Id)}/events";

        // One fixed parameter set for the first and every later sync: a sync token is only valid with the parameters that made it.
        List<EventDto> items;
        string? syncToken;
        bool wellFormed;
        try
        {
            (items, syncToken, wellFormed) = await ReadEventsAsync(path, state?.SyncToken, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleNotFoundException ex)
        {
            return NotFound<CalendarEvent>(previous, ex);
        }

        if (string.IsNullOrEmpty(syncToken))
        {
            throw new SourceDataException("Google ended a sync without a position to continue from.");
        }

        var warnings = new List<SyncWarning>();
        var latest = new Dictionary<string, EventDto>();
        var overrides = new List<EventDto>();
        foreach (var dto in items.Where(i => !string.IsNullOrEmpty(i.Id) && !EventMapper.IsIgnored(i)))
        {
            if (string.IsNullOrEmpty(dto.RecurringEventId))
            {
                latest[dto.Id!] = dto;
            }
            else
            {
                overrides.Add(dto);
            }
        }

        var upserts = new List<CalendarEvent>();
        var deleted = new List<string>();
        foreach (var (id, dto) in latest)
        {
            if (EventMapper.IsRemoval(dto))
            {
                deleted.Add(id);
            }
            else if (_eventMapper.MapEvent(collection.Id, dto, warnings) is { } mapped)
            {
                upserts.Add(mapped);
            }
        }

        foreach (var dto in overrides)
        {
            if (_eventMapper.MapEvent(collection.Id, dto, warnings) is { } mapped)
            {
                upserts.Add(mapped);
            }
        }

        var newState = new SyncState(JsonSerializer.Serialize(new CalendarState { SyncToken = syncToken }, GoogleJson.Default.CalendarState));
        return state is null
            ? SyncResult.Snapshot<CalendarEvent>(upserts, newState, warnings, confirmedEmpty: wellFormed && items.Count == 0)
            : SyncResult.Delta<CalendarEvent>(upserts, deleted, newState, warnings);
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var now = TimeProvider.GetUtcNow();
        var state = ParseState(previous, GoogleJson.Default.TaskState);

        // A timestamp cursor cannot prove that nothing was missed, so a full snapshot is taken now and then.
        var snapshot = state is null || now - state.FullAt >= FullSnapshotInterval;
        var path = $"lists/{Uri.EscapeDataString(collection.Id)}/tasks";

        List<TaskDto> items;
        bool wellFormed;
        try
        {
            (items, wellFormed) = await ReadTasksAsync(path, snapshot ? null : state!.UpdatedMin - Overlap, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleNotFoundException ex)
        {
            return NotFound<TaskItem>(previous, ex);
        }

        var warnings = new List<SyncWarning>();
        var latest = new Dictionary<string, TaskDto>();
        foreach (var dto in items.Where(i => !string.IsNullOrEmpty(i.Id)))
        {
            latest[dto.Id!] = dto;
        }

        var upserts = new List<TaskItem>();
        var deleted = new List<string>();
        foreach (var (id, dto) in latest)
        {
            if (dto.Deleted)
            {
                deleted.Add(id);
            }
            else if (_taskMapper.MapTask(collection.Id, dto, warnings) is { } mapped)
            {
                upserts.Add(mapped);
            }
        }

        // The cursor comes from the server's own timestamps, never the local clock, so clock skew cannot lose changes.
        var newest = items.Select(i => Text.ParseInstant(i.Updated)).Where(u => u is not null).Max();
        var updatedMin = newest is { } seen && (snapshot || seen > state!.UpdatedMin) ? seen : snapshot ? now : state!.UpdatedMin;
        var newState = new SyncState(JsonSerializer.Serialize(
            new TaskState { UpdatedMin = updatedMin, FullAt = snapshot ? now : state!.FullAt },
            GoogleJson.Default.TaskState));
        return snapshot
            ? SyncResult.Snapshot(upserts, newState, warnings, confirmedEmpty: wellFormed && items.Count == 0)
            : SyncResult.Delta(upserts, deleted, newState, warnings);
    }

    private static T? ParseState<T>(SyncState? previous, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (previous is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(previous.Token, typeInfo) is { } state && StateVersion(state) == 1
                ? state
                : throw new SyncStateExpiredException("The saved Google sync position has an unknown version.");
        }
        catch (JsonException)
        {
            throw new SyncStateExpiredException("The saved Google sync position could not be read.");
        }
    }

    private static int StateVersion(object state) => state switch
    {
        CalendarState c => c.V,
        TaskState t => t.V,
        _ => 0,
    };

    /// <summary>A calendar or list that is gone keeps what is cached and adds a warning.</summary>
    private static SyncResult<T> NotFound<T>(SyncState? previous, GoogleNotFoundException ex) =>
        SyncResult.Delta<T>([], [], previous ?? new SyncState("{\"v\":1}"), [new SyncWarning(null, ex.Message)]);

    private async Task<(List<EventDto> Items, string? SyncToken, bool WellFormed)> ReadEventsAsync(string path, string? syncToken, CancellationToken cancellationToken)
    {
        var items = new List<EventDto>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = GoogleClient.Api(GoogleClient.CalendarRoot, path, ("maxResults", "2500"), ("fields", EventFields), ("syncToken", syncToken), ("pageToken", pageToken));
            var reply = await RetryAsync(ct => _client.GetAsync(url, GoogleJson.Default.ListPageEventDto, ct), cancellationToken: cancellationToken).ConfigureAwait(false);
            items.AddRange(reply.Items ?? []);
            if (!string.IsNullOrEmpty(reply.NextPageToken))
            {
                pageToken = reply.NextPageToken;
                continue;
            }

            // The new token replaces the old one only after the last page arrived, so a failed sync leaves the state untouched.
            return (items, reply.NextSyncToken, reply.Kind == "calendar#events");
        }

        throw TooManyPages();
    }

    private async Task<(List<TaskDto> Items, bool WellFormed)> ReadTasksAsync(string path, DateTimeOffset? updatedMin, CancellationToken cancellationToken)
    {
        var items = new List<TaskDto>();
        var wellFormed = true;
        string? pageToken = null;
        var since = updatedMin?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        for (var page = 0; page < MaxPages; page++)
        {
            // Without showHidden and showCompleted, tasks the user completed or cleared would vanish; showDeleted gives tombstones.
            var url = GoogleClient.Api(
                GoogleClient.TasksRoot,
                path,
                ("maxResults", "100"),
                ("showCompleted", "true"),
                ("showHidden", "true"),
                ("showDeleted", updatedMin is null ? null : "true"),
                ("updatedMin", since),
                ("fields", TaskFields),
                ("pageToken", pageToken));
            var reply = await RetryAsync(ct => _client.GetAsync(url, GoogleJson.Default.ListPageTaskDto, ct), cancellationToken: cancellationToken).ConfigureAwait(false);
            wellFormed &= reply.Kind == "tasks#tasks";
            items.AddRange(reply.Items ?? []);
            if (string.IsNullOrEmpty(reply.NextPageToken))
            {
                return (items, wellFormed);
            }

            pageToken = reply.NextPageToken;
        }

        throw TooManyPages();
    }

    private async Task<List<T>> ReadAllAsync<T>(Func<string?, Uri> address, JsonTypeInfo<ListPage<T>> typeInfo, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = address(pageToken);
            var reply = await RetryAsync(ct => _client.GetAsync(url, typeInfo, ct), cancellationToken: cancellationToken).ConfigureAwait(false);
            items.AddRange(reply.Items ?? []);
            if (string.IsNullOrEmpty(reply.NextPageToken))
            {
                return items;
            }

            pageToken = reply.NextPageToken;
        }

        throw TooManyPages();
    }

    private static SourceDataException TooManyPages() =>
        new("Google returned more pages than the app reads in one sync.") { Diagnostics = $"Stopped after {MaxPages} pages" };
}
