using AllMyTasks.Core.Calendar;

namespace AllMyTasks.Core.Views;

/// <summary>What counts as a conflict. See docs/design/conflict-highlight.md.</summary>
/// <param name="CountAllDayBusy">Also count all-day events that the source marks as busy. Off by default: an all-day "Holiday" is not a meeting.</param>
public sealed record ConflictOptions(bool CountAllDayBusy = false);

/// <summary>Entries that overlap each other, directly or through a chain of overlaps (A with B, B with C).</summary>
public sealed record ConflictGroup(IReadOnlyList<EventEntry> Entries)
{
    /// <summary>The earliest start in the group.</summary>
    public DateTimeOffset Start => Entries.Min(e => e.Start);
}

/// <summary>The conflicts among a set of entries, with a lookup from an entry to the entries it directly overlaps.</summary>
public sealed class ConflictMap
{
    private readonly Dictionary<(Model.ItemKey Key, DateTimeOffset Start), List<EventEntry>> _overlaps;

    internal ConflictMap(IReadOnlyList<ConflictGroup> groups, Dictionary<(Model.ItemKey, DateTimeOffset), List<EventEntry>> overlaps)
    {
        Groups = groups;
        _overlaps = overlaps;
    }

    /// <summary>No conflicts.</summary>
    public static ConflictMap Empty { get; } = new([], []);

    /// <summary>Groups in order of their first start.</summary>
    public IReadOnlyList<ConflictGroup> Groups { get; }

    public int Count => Groups.Count;

    /// <summary>The entries that directly overlap <paramref name="entry"/>, in start order. Empty when it conflicts with nothing.</summary>
    public IReadOnlyList<EventEntry> OverlapsWith(AgendaEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _overlaps.TryGetValue((entry.Key, entry.Start), out var list) ? list : [];
    }

    public bool HasConflict(AgendaEntry entry) => OverlapsWith(entry).Count > 0;
}

/// <summary>
/// Finds events that overlap in time, whichever source they come from. Two events conflict when they overlap by at
/// least one minute, both are busy, and neither is cancelled, tentative or free. All-day events and tasks are left out
/// unless <see cref="ConflictOptions.CountAllDayBusy"/> asks for busy all-day events. Events of one source count.
/// A pure function over the entries a view already has, so hidden collections never take part.
/// </summary>
public static class ConflictFinder
{
    private static readonly TimeSpan MinimumOverlap = TimeSpan.FromMinutes(1);

    public static ConflictMap Find(IEnumerable<AgendaEntry> entries, ConflictOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new ConflictOptions();

        // Real instants, so a DST day is as long as it really is. Zero-length and sub-minute events cannot overlap by a minute.
        var candidates = entries
            .OfType<EventEntry>()
            .Where(e => Counts(e, options) && e.End - e.Start >= MinimumOverlap)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ToList();

        var groups = new List<ConflictGroup>();
        var overlaps = new Dictionary<(Model.ItemKey, DateTimeOffset), List<EventEntry>>();
        var cluster = new List<EventEntry>();
        var active = new List<EventEntry>();

        void Flush()
        {
            if (cluster.Count > 1)
            {
                groups.Add(new ConflictGroup([.. cluster]));
            }

            cluster.Clear();
        }

        foreach (var entry in candidates)
        {
            // An active entry that ends less than a minute after this start cannot overlap it, or any later one, by a minute.
            active.RemoveAll(a => a.End - entry.Start < MinimumOverlap);
            if (active.Count == 0)
            {
                Flush();
            }

            foreach (var other in active)
            {
                Add(overlaps, entry, other);
                Add(overlaps, other, entry);
            }

            cluster.Add(entry);
            active.Add(entry);
        }

        Flush();
        return groups.Count == 0 ? ConflictMap.Empty : new ConflictMap(groups, overlaps);
    }

    private static bool Counts(EventEntry entry, ConflictOptions options)
    {
        var ev = entry.Occurrence.Event;
        if (ev.Status is EventStatus.Cancelled or EventStatus.Tentative || ev.Availability != Availability.Busy)
        {
            return false;
        }

        return !entry.IsAllDay || options.CountAllDayBusy;
    }

    private static void Add(Dictionary<(Model.ItemKey, DateTimeOffset), List<EventEntry>> map, EventEntry entry, EventEntry other)
    {
        var key = (entry.Key, entry.Start);
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(other);
    }
}
