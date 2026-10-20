using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>
/// Reads the store, expands recurrences for the range, converts to the display zone, drops hidden
/// collections and sorts.
/// </summary>
public sealed class AgendaService(
    IItemStore store,
    ISourceRepository sources,
    IRecurrenceExpander expander,
    TimeProvider clock) : IAgendaService
{
    public async Task<IReadOnlyList<AgendaEntry>> GetEntriesAsync(ViewQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var zone = query.DisplayZone;
        var rangeStart = query.From.StartOfDay(zone);
        var rangeEnd = query.ToExclusive.StartOfDay(zone);

        var collections = (await sources.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => (c.SourceId, c.Id));
        var entries = new List<AgendaEntry>();

        if (query.Filter.ShowEvents)
        {
            var candidates = await store.GetEventCandidatesAsync(rangeStart, rangeEnd, zone, cancellationToken).ConfigureAwait(false);
            foreach (var series in candidates.GroupBy(e => e.Key.Master))
            {
                if (!TryGetVisible(collections, series.Key.SourceId, series.Key.CollectionId, out var collection))
                {
                    continue;
                }

                foreach (var occurrence in Occurrences(series, rangeStart, rangeEnd, zone))
                {
                    entries.Add(ToEntry(occurrence, collection, zone));
                }
            }
        }

        if (query.Filter.ShowTasks)
        {
            var now = clock.GetUtcNow();
            var tasks = await store.GetTasksAsync(cancellationToken).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                if (task.Due is null
                    || !TryGetVisible(collections, task.Key.SourceId, task.Key.CollectionId, out var collection)
                    || (!task.IsOpen && !query.Filter.ShowCompleted))
                {
                    continue;
                }

                var entry = ToEntry(task, collection, zone, task.IsOverdue(now, zone));
                var inRange = entry.Start < rangeEnd && (entry.End > rangeStart || entry.Start >= rangeStart);
                if (inRange || (query.IncludeOverdue && entry.IsOverdue && entry.Start < rangeStart))
                {
                    entries.Add(entry);
                }
            }
        }

        return [.. entries
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.IsAllDay)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private IEnumerable<EventOccurrence> Occurrences(
        IEnumerable<CalendarEvent> series,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo zone)
    {
        var rows = series.ToList();
        var master = rows.FirstOrDefault(e => !e.Key.IsOverride);
        if (master is null)
        {
            // Overrides whose master is missing (e.g. master deleted): show them as plain events.
            return rows
                .Where(e => e.Status != EventStatus.Cancelled && e.Time.Overlaps(rangeStart, rangeEnd, zone))
                .Select(e => new EventOccurrence(e, e.Time, IsOverride: true));
        }

        return expander.Expand(master, rows.Where(e => e.Key.IsOverride), rangeStart, rangeEnd, zone);
    }

    private static bool TryGetVisible(
        Dictionary<(string, string), CollectionInfo> collections,
        string sourceId,
        string collectionId,
        out CollectionInfo collection)
    {
        // Items whose collection is unknown (not yet saved) are shown rather than silently hidden.
        if (collections.TryGetValue((sourceId, collectionId), out var found))
        {
            collection = found;
            return found.IsVisible;
        }

        collection = new CollectionInfo(sourceId, collectionId, string.Empty, CollectionKind.Calendar);
        return true;
    }

    private static EventEntry ToEntry(EventOccurrence occurrence, CollectionInfo collection, TimeZoneInfo zone)
    {
        var start = occurrence.Time.StartInstant(zone);
        var end = occurrence.Time.EndInstant(zone);
        var isAllDay = occurrence.Time is AllDayEvent;
        var (first, last) = isAllDay
            ? (((AllDayEvent)occurrence.Time).Start, ((AllDayEvent)occurrence.Time).EndExclusive)
            : DayRange(start, end, zone);
        return new EventEntry(occurrence, collection.Color, collection.DisplayName, start, end, isAllDay, first, last);
    }

    private static TaskEntry ToEntry(TaskItem task, CollectionInfo collection, TimeZoneInfo zone, bool isOverdue)
    {
        switch (task.Due)
        {
            case DueDate date:
                return new TaskEntry(task, collection.Color, collection.DisplayName, date.Date.StartOfDay(zone), date.Date.AddDays(1).StartOfDay(zone), true, date.Date, date.Date.AddDays(1), isOverdue);
            case DueDateTime at:
                var (first, last) = DayRange(at.At, at.At, zone);
                return new TaskEntry(task, collection.Color, collection.DisplayName, at.At, at.At, false, first, last, isOverdue);
            default:
                throw new InvalidOperationException("Task has no due value.");
        }
    }

    /// <summary>The days a timed span touches. An end exactly at midnight does not touch the next day.</summary>
    internal static (DateOnly First, DateOnly LastExclusive) DayRange(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var endLocal = TimeZoneInfo.ConvertTime(end, zone);
        var last = DateOnly.FromDateTime(endLocal.DateTime);
        if (end > start && endLocal.TimeOfDay == TimeSpan.Zero)
        {
            return (first, last);
        }

        return (first, last.AddDays(1));
    }
}
