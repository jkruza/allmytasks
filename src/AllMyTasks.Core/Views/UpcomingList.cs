using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

public enum DayGroupKind
{
    Overdue,
    Today,
    Tomorrow,
    Date,
}

/// <summary>A heading and the segments under it.</summary>
/// <param name="Kind">Which heading to show.</param>
/// <param name="Date">The day, for <see cref="DayGroupKind.Today"/>, <see cref="DayGroupKind.Tomorrow"/> and <see cref="DayGroupKind.Date"/>.</param>
/// <param name="Segments">All-day first, then by time.</param>
public sealed record DayGroup(DayGroupKind Kind, DateOnly? Date, IReadOnlyList<DaySegment> Segments);

/// <summary>The "what's next" list for the coming days.</summary>
public sealed record UpcomingList(IReadOnlyList<DayGroup> Groups)
{
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>
    /// Builds groups for <paramref name="days"/> days from today. Items already over are left out, empty
    /// days are skipped, and overdue open tasks form the first group.
    /// </summary>
    public static UpcomingList Build(IEnumerable<AgendaEntry> entries, DateTimeOffset now, TimeZoneInfo zone, int days = 14)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var today = now.Today(zone);
        var groups = new List<DayGroup>();

        var overdue = list.OfType<TaskEntry>().Where(t => t.IsOverdue).OrderBy(t => t.Start)
            .Select(t => new DaySegment(t, today, t.Start, t.End, false, false)).ToList();
        if (overdue.Count > 0)
        {
            groups.Add(new DayGroup(DayGroupKind.Overdue, null, overdue));
        }

        for (var i = 0; i < days; i++)
        {
            var day = today.AddDays(i);
            var segments = list
                .Where(e => e is not TaskEntry { IsOverdue: true })
                .Select(e => DaySegment.On(e, day, zone))
                .OfType<DaySegment>()
                .Where(s => !s.IsPast(now) && !(s.Start == s.End && s.Start < now))
                .OrderByDescending(s => s.IsAllDay)
                .ThenBy(s => s.Start)
                .ThenBy(s => s.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (segments.Count > 0)
            {
                var kind = i switch { 0 => DayGroupKind.Today, 1 => DayGroupKind.Tomorrow, _ => DayGroupKind.Date };
                groups.Add(new DayGroup(kind, day, segments));
            }
        }

        return new UpcomingList(groups);
    }
}
