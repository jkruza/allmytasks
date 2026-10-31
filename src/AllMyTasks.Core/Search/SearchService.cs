using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Search;

/// <summary>What to look for.</summary>
/// <param name="Text">The search text; see <see cref="TextQuery"/>. Blank finds nothing.</param>
/// <param name="Collections">Only search these collections; null searches all visible ones.</param>
/// <param name="Limit">The most events and the most tasks to return (each group on its own).</param>
public sealed record SearchQuery(string Text, IReadOnlySet<CollectionRef>? Collections = null, int Limit = 100);

/// <summary>An event found by a search. A recurring series is one hit, placed at its next occurrence.</summary>
/// <param name="Event">The event, or the master of a series.</param>
/// <param name="Start">Start of the event, or of the series' next occurrence (the latest past one when none is left).</param>
/// <param name="IsAllDay">Whether it covers whole days.</param>
/// <param name="IsPast">Whether it has ended.</param>
/// <param name="IsRecurring">Whether the hit stands for a series.</param>
/// <param name="CollectionName">Name of its calendar.</param>
/// <param name="SourceName">Name of its source.</param>
/// <param name="Color">Calendar colour.</param>
public sealed record EventHit(
    CalendarEvent Event,
    DateTimeOffset Start,
    bool IsAllDay,
    bool IsPast,
    bool IsRecurring,
    string CollectionName,
    string SourceName,
    string? Color);

/// <summary>A task found by a search.</summary>
public sealed record TaskHit(TaskItem Task, string CollectionName, string SourceName, string? Color, bool IsOverdue);

/// <summary>Search results.</summary>
/// <param name="Events">Events, upcoming first and then past ones, most recent first.</param>
/// <param name="Tasks">Tasks by due value, undated last.</param>
/// <param name="HiddenByFilter">How many more events and tasks matched in hidden collections.</param>
public sealed record SearchResults(IReadOnlyList<EventHit> Events, IReadOnlyList<TaskHit> Tasks, int HiddenByFilter)
{
    public static SearchResults Empty { get; } = new([], [], 0);

    public int Count => Events.Count + Tasks.Count;
}

public interface ISearchService
{
    /// <summary>Searches the cached events and tasks. Never touches the network.</summary>
    Task<SearchResults> SearchAsync(SearchQuery query, TimeZoneInfo displayZone, CancellationToken cancellationToken = default);
}

/// <summary>
/// Searches the local cache in memory. A personal calendar is thousands of items, not millions, so a scan is fast enough;
/// see docs/design/search-and-saved-filters.md for when to move to SQLite FTS5.
/// </summary>
public sealed class SearchService(IItemStore store, ISourceRepository sources, IRecurrenceExpander expander, TimeProvider clock) : ISearchService
{
    // The cache holds only what the sync window brought in, so a wide range simply means "all of it".
    private const int RangeYears = 30;
    private const int NextOccurrenceYears = 3;

    public async Task<SearchResults> SearchAsync(SearchQuery query, TimeZoneInfo displayZone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(displayZone);
        var text = TextQuery.Parse(query.Text);
        if (text.IsEmpty)
        {
            return SearchResults.Empty;
        }

        var collections = (await sources.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => (c.SourceId, c.Id));
        var sourceNames = (await sources.GetSourcesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.Id, s => s.DisplayName);
        var now = clock.GetUtcNow();
        var hidden = 0;

        // Items of a collection the store does not know yet are searched rather than hidden, as in the other views.
        bool Wanted(ItemKey key, out CollectionInfo? collection)
        {
            collections.TryGetValue((key.SourceId, key.CollectionId), out collection);
            if (query.Collections is { Count: > 0 } only && !only.Contains(new CollectionRef(key.SourceId, key.CollectionId)))
            {
                return false;
            }

            if (collection is { IsVisible: false })
            {
                hidden++;
                return false;
            }

            return true;
        }

        string SourceName(ItemKey key) => sourceNames.GetValueOrDefault(key.SourceId, string.Empty);

        var events = new List<EventHit>();
        var candidates = await store.GetEventCandidatesAsync(now.AddYears(-RangeYears), now.AddYears(RangeYears), displayZone, cancellationToken)
            .ConfigureAwait(false);
        foreach (var series in candidates.GroupBy(e => e.Key.Master))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = series.ToList();
            var master = rows.FirstOrDefault(e => !e.Key.IsOverride);

            // Overrides whose master is missing are searched as plain events.
            IReadOnlyList<CalendarEvent> singles = master is null ? rows : new[] { master };
            foreach (var item in singles)
            {
                if (!Wanted(item.Key, out var collection))
                {
                    continue;
                }

                var collectionName = collection?.DisplayName ?? string.Empty;
                if (!text.Matches(EventFields(item, collectionName)))
                {
                    continue;
                }

                IEnumerable<CalendarEvent> overrides = master is null ? Array.Empty<CalendarEvent>() : rows.Where(e => e.Key.IsOverride);
                events.Add(ToHit(item, overrides, collection, SourceName(item.Key), displayZone, now));
            }
        }

        var tasks = new List<TaskHit>();
        foreach (var task in await store.GetTasksAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Wanted(task.Key, out var collection))
            {
                continue;
            }

            if (text.Matches([task.Title, task.Notes, .. task.Labels, collection?.DisplayName]))
            {
                tasks.Add(new TaskHit(task, collection?.DisplayName ?? string.Empty, SourceName(task.Key), collection?.Color, task.IsOverdue(now, displayZone)));
            }
        }

        var limit = Math.Max(query.Limit, 0);
        var upcoming = events.Where(e => !e.IsPast).OrderBy(e => e.Start).ThenBy(e => e.Event.Title, StringComparer.CurrentCultureIgnoreCase);
        var past = events.Where(e => e.IsPast).OrderByDescending(e => e.Start).ThenBy(e => e.Event.Title, StringComparer.CurrentCultureIgnoreCase);
        var datedTasks = tasks.Where(t => t.Task.Due is not null).OrderBy(t => t.Task.Due!.SortKey(displayZone));
        var undatedTasks = tasks.Where(t => t.Task.Due is null);

        return new SearchResults(
            [.. upcoming.Concat(past).Take(limit)],
            [.. datedTasks.Concat(undatedTasks).Take(limit)],
            hidden);
    }

    private static IEnumerable<string?> EventFields(CalendarEvent item, string collectionName)
    {
        yield return item.Title;
        yield return item.Description;
        yield return item.Location;
        yield return collectionName;
        yield return item.Organizer?.Name;
        yield return item.Organizer?.Email;
        foreach (var attendee in item.Attendees)
        {
            yield return attendee.Name;
            yield return attendee.Email;
        }
    }

    private EventHit ToHit(
        CalendarEvent item,
        IEnumerable<CalendarEvent> overrides,
        CollectionInfo? collection,
        string sourceName,
        TimeZoneInfo zone,
        DateTimeOffset now)
    {
        var time = item.Time;
        var recurring = item.Recurrence is not null && !item.Key.IsOverride;
        if (recurring)
        {
            var modified = overrides.ToList();
            var next = expander.Expand(item, modified, now, now.AddYears(NextOccurrenceYears), zone);
            var shown = next.Count > 0
                ? next[0]
                : expander.Expand(item, modified, now.AddYears(-NextOccurrenceYears), now, zone).LastOrDefault();
            if (shown is not null)
            {
                time = shown.Time;
            }
        }

        var start = time.StartInstant(zone);
        var end = time.EndInstant(zone);
        return new EventHit(item, start, time is AllDayEvent, end <= now && start < now, recurring, collection?.DisplayName ?? string.Empty, sourceName, collection?.Color);
    }
}
