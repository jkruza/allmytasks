using System.Globalization;
using System.Text;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>Which duplicates to collapse (docs/design/duplicate-detection.md).</summary>
/// <param name="Events">Collapse duplicate events. On by default.</param>
/// <param name="Tasks">Collapse duplicate tasks. Off by default, because task titles repeat more often than events do.</param>
/// <param name="SourcePriority">Source ids, best first. A source that is not listed ranks after all listed ones, in the order given by <c>fallbackOrder</c>.</param>
public sealed record DuplicateOptions(bool Events = true, bool Tasks = false, IReadOnlyList<string>? SourcePriority = null)
{
    public static DuplicateOptions Off { get; } = new(false, false);
}

/// <summary>
/// Hides the same event or task when it arrives from two sources, and keeps the one from the source the user ranks
/// highest. Nothing is deleted: the cache keeps both items and only the view shows one. The rules are deliberately
/// strict, because hiding something real is worse than showing a duplicate.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>
    /// Returns the entries with duplicates removed; each survivor lists the collections it absorbed in
    /// <see cref="AgendaEntry.AlsoIn"/>. The order of the surviving entries is kept.
    /// </summary>
    /// <param name="entries">Entries of one view, already expanded and in the display zone.</param>
    /// <param name="options">What to collapse and the source ranking.</param>
    /// <param name="fallbackOrder">Source ids in the order they were added; ranks sources that are not in the priority list.</param>
    public static IReadOnlyList<AgendaEntry> Collapse(IReadOnlyList<AgendaEntry> entries, DuplicateOptions options, IReadOnlyList<string>? fallbackOrder = null)
    {
        Guard.NotNull(entries);
        Guard.NotNull(options);
        if (!options.Events && !options.Tasks || entries.Count < 2)
        {
            return entries;
        }

        var rank = Rank(options.SourcePriority, fallbackOrder);
        var parent = Enumerable.Range(0, entries.Count).ToArray();

        // Only entries that start in the same minute can match, so compare inside those buckets.
        foreach (var bucket in Bucket(entries, options))
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    if (AreDuplicates(entries[bucket[i]], entries[bucket[j]]))
                    {
                        Union(parent, bucket[i], bucket[j]);
                    }
                }
            }
        }

        var groups = Enumerable.Range(0, entries.Count).GroupBy(i => Find(parent, i)).Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0)
        {
            return entries;
        }

        var hidden = new HashSet<int>();
        var replaced = new Dictionary<int, AgendaEntry>();
        foreach (var group in groups)
        {
            // The best-ranked source survives; ties go to the earlier entry so the result is stable.
            var survivor = group.OrderBy(i => rank(entries[i].Key.SourceId)).ThenBy(i => i).First();
            var others = group.Where(i => i != survivor).ToList();
            hidden.UnionWith(others);
            var names = others.Select(i => entries[i].CollectionName).Where(n => n.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            replaced[survivor] = entries[survivor] with { AlsoIn = names };
        }

        var result = new List<AgendaEntry>(entries.Count - hidden.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!hidden.Contains(i))
            {
                result.Add(replaced.TryGetValue(i, out var entry) ? entry : entries[i]);
            }
        }

        return result;
    }

    /// <summary>True when two entries from different sources are the same event or the same task.</summary>
    public static bool AreDuplicates(AgendaEntry a, AgendaEntry b)
    {
        Guard.NotNull(a);
        Guard.NotNull(b);
        if (a.Key.SourceId == b.Key.SourceId || a.IsAllDay != b.IsAllDay || !SameMinute(a.Start, b.Start) || !SameMinute(a.End, b.End))
        {
            return false;
        }

        return (a, b) switch
        {
            (EventEntry x, EventEntry y) => SameEvent(x.Occurrence.Event, y.Occurrence.Event),
            (TaskEntry x, TaskEntry y) => SameTask(x.Task, y.Task),
            _ => false,
        };
    }

    /// <summary>
    /// The same start and end are already known to match. An iCalendar UID is enough on its own, because invitations keep it
    /// across calendars; the start is still compared first, since every occurrence of a series shares one UID. Without a UID,
    /// the title must match as well as one more sign: the meeting link, the organiser or the location.
    /// </summary>
    private static bool SameEvent(CalendarEvent a, CalendarEvent b)
    {
        if (a.Status == EventStatus.Cancelled || b.Status == EventStatus.Cancelled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(a.Uid) && string.Equals(a.Uid, b.Uid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!SameTitle(a.Title, b.Title))
        {
            return false;
        }

        return (a.MeetingUrl is not null && a.MeetingUrl == b.MeetingUrl)
            || (!string.IsNullOrWhiteSpace(a.Organizer?.Email) && string.Equals(a.Organizer.Email.Trim(), b.Organizer?.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
            || (Normalize(a.Location) is { Length: > 0 } location && location == Normalize(b.Location));
    }

    /// <summary>Same title and due value are known; a task also needs the same link, since two lists can both hold "Pay rent".</summary>
    private static bool SameTask(TaskItem a, TaskItem b) =>
        SameTitle(a.Title, b.Title) && a.Url is not null && a.Url == b.Url;

    private static bool SameTitle(string a, string b)
    {
        var x = Normalize(a);
        return x.Length > 0 && x == Normalize(b);
    }

    private static bool SameMinute(DateTimeOffset a, DateTimeOffset b) => Minute(a) == Minute(b);

    private static long Minute(DateTimeOffset value) => value.UtcTicks / TimeSpan.TicksPerMinute;

    /// <summary>Trimmed, case-folded, accents removed and runs of white space collapsed.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static IEnumerable<List<int>> Bucket(IReadOnlyList<AgendaEntry> entries, DuplicateOptions options)
    {
        var buckets = new Dictionary<(long Minute, bool IsTask), List<int>>();
        for (var i = 0; i < entries.Count; i++)
        {
            var isTask = entries[i] is TaskEntry;
            if (isTask ? !options.Tasks : !options.Events)
            {
                continue;
            }

            var key = (Minute(entries[i].Start), isTask);
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add(i);
        }

        return buckets.Values.Where(b => b.Count > 1);
    }

    private static Func<string, int> Rank(IReadOnlyList<string>? priority, IReadOnlyList<string>? fallback)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in (priority ?? []).Concat(fallback ?? []))
        {
            order.TryAdd(id, order.Count);
        }

        return id => order.TryGetValue(id, out var position) ? position : int.MaxValue;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    private static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);
}
