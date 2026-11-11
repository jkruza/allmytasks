using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>When a day counts as heavy (docs/design/workload-glance.md).</summary>
/// <param name="MeetingLimit">A day with at least this much meeting time is heavy.</param>
/// <param name="TasksLimit">A day with at least this many tasks due is heavy.</param>
public sealed record WorkloadOptions(TimeSpan MeetingLimit, int TasksLimit)
{
    public static WorkloadOptions Default { get; } = new(TimeSpan.FromHours(6), 8);
}

/// <summary>One day of the workload strip.</summary>
/// <param name="Date">The day, in the display zone.</param>
/// <param name="MeetingTime">Time in timed, busy events; overlaps count once.</param>
/// <param name="TasksDue">Open tasks due that day, not counting overdue ones.</param>
/// <param name="IsHeavy">Meeting time or tasks due reached the limit.</param>
public sealed record DayLoad(DateOnly Date, TimeSpan MeetingTime, int TasksDue, bool IsHeavy);

/// <summary>How heavy the coming days are, built from the entries a view already has.</summary>
/// <param name="Days">One entry per day, starting today.</param>
/// <param name="Overdue">Open tasks that are overdue.</param>
public sealed record WorkloadSummary(IReadOnlyList<DayLoad> Days, int Overdue)
{
    /// <summary>
    /// Counts what <paramref name="entries"/> hold. Hidden collections and duplicates are already out of the entries, and so
    /// are completed tasks unless the filter asked for them, which are skipped here too. All-day, free, cancelled and declined events
    /// are not meeting time. A day is its real length in <paramref name="zone"/>, so a daylight-saving day is 23 or 25 hours.
    /// </summary>
    public static WorkloadSummary Build(IEnumerable<AgendaEntry> entries, DateOnly today, TimeZoneInfo zone, WorkloadOptions options, int days = 7)
    {
        Guard.NotNull(entries);
        Guard.NotNull(zone);
        Guard.NotNull(options);
        var list = entries as IReadOnlyList<AgendaEntry> ?? [.. entries];
        var meetings = list
            .OfType<EventEntry>()
            .Where(e => !e.IsAllDay && e.End > e.Start && e.Occurrence.Event is { Availability: Availability.Busy, Status: not EventStatus.Cancelled })
            .ToList();
        var tasks = list.OfType<TaskEntry>().Where(t => t.Task.IsOpen).ToList();

        var result = new List<DayLoad>(days);
        for (var i = 0; i < days; i++)
        {
            var day = today.AddDays(i);
            var meetingTime = MeetingTime(meetings, day.StartOfDay(zone), day.AddDays(1).StartOfDay(zone));
            var due = tasks.Count(t => !t.IsOverdue && t.FirstDay == day);
            result.Add(new DayLoad(day, meetingTime, due, meetingTime >= options.MeetingLimit || due >= options.TasksLimit));
        }

        return new WorkloadSummary(result, tasks.Count(t => t.IsOverdue));
    }

    /// <summary>Union of the events' intervals clipped to the day, so overlapping meetings are counted once.</summary>
    private static TimeSpan MeetingTime(List<EventEntry> meetings, DateTimeOffset dayStart, DateTimeOffset dayEnd)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? runStart = null;
        DateTimeOffset runEnd = default;
        foreach (var (start, end) in meetings
            .Where(e => e.Start < dayEnd && e.End > dayStart)
            .Select(e => (Start: e.Start < dayStart ? dayStart : e.Start, End: e.End > dayEnd ? dayEnd : e.End))
            .OrderBy(i => i.Start))
        {
            if (runStart is null)
            {
                (runStart, runEnd) = (start, end);
            }
            else if (start <= runEnd)
            {
                runEnd = end > runEnd ? end : runEnd;
            }
            else
            {
                total += runEnd - runStart.Value;
                (runStart, runEnd) = (start, end);
            }
        }

        return runStart is null ? total : total + (runEnd - runStart.Value);
    }
}
