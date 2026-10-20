using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>One day, ready to render as a vertical list.</summary>
/// <param name="Date">The day shown.</param>
/// <param name="AllDay">All-day events and date-only tasks.</param>
/// <param name="Timed">Timed events and tasks, ordered by start. Overlaps are stacked, not side by side.</param>
/// <param name="Overdue">Open tasks past due. Only filled when <paramref name="Date"/> is today.</param>
/// <param name="NowIndex">For today: the position in <paramref name="Timed"/> where the "now" marker goes; otherwise null.</param>
public sealed record DayAgenda(
    DateOnly Date,
    IReadOnlyList<DaySegment> AllDay,
    IReadOnlyList<DaySegment> Timed,
    IReadOnlyList<DaySegment> Overdue,
    int? NowIndex)
{
    public bool IsEmpty => AllDay.Count == 0 && Timed.Count == 0 && Overdue.Count == 0;

    /// <summary>
    /// Builds the agenda for <paramref name="date"/>. Overdue open tasks are listed on today only, never on
    /// the day they were due.
    /// </summary>
    public static DayAgenda Build(DateOnly date, IEnumerable<AgendaEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var today = now.Today(zone);
        var isToday = date == today;

        var onDay = entries
            .Select(e => (Entry: e, Segment: DaySegment.On(e, date, zone)))
            .Where(x => x.Segment is not null)
            .Select(x => (x.Entry, Segment: x.Segment!))
            .ToList();

        var overdue = isToday
            ? entries.OfType<TaskEntry>().Where(t => t.IsOverdue).OrderBy(t => t.Start).Select(t => OverdueSegment(t, date)).ToList()
            : [];

        var regular = onDay.Where(x => x.Entry is not TaskEntry { IsOverdue: true }).Select(x => x.Segment).ToList();
        var allDay = regular.Where(s => s.IsAllDay).ToList();
        var timed = regular.Where(s => !s.IsAllDay).OrderBy(s => s.Start).ThenBy(s => s.End).ToList();

        return new DayAgenda(date, allDay, timed, overdue, isToday ? timed.Count(s => s.Start <= now) : null);
    }

    private static DaySegment OverdueSegment(TaskEntry task, DateOnly today) =>
        new(task, today, task.Start, task.End, ContinuedFromBefore: false, ContinuesAfter: false);
}
