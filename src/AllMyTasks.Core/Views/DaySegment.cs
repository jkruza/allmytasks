using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>
/// The part of an <see cref="AgendaEntry"/> that falls on one day. A timed event crossing midnight has
/// one segment per day, each flagged as continued from before and/or continuing after.
/// </summary>
/// <param name="Entry">The entry this is part of.</param>
/// <param name="Day">The day, in the display zone.</param>
/// <param name="Start">Start of the part, clipped to the day.</param>
/// <param name="End">End of the part, clipped to the day.</param>
/// <param name="ContinuedFromBefore">The entry started on an earlier day.</param>
/// <param name="ContinuesAfter">The entry goes on into a later day.</param>
public sealed record DaySegment(
    AgendaEntry Entry,
    DateOnly Day,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool ContinuedFromBefore,
    bool ContinuesAfter)
{
    public bool IsAllDay => Entry.IsAllDay;

    /// <summary>True when the segment is over at <paramref name="now"/>. All-day items are over after their day.</summary>
    public bool IsPast(DateTimeOffset now) => End <= now;

    /// <summary>The segment of <paramref name="entry"/> on <paramref name="day"/>, or null if it does not touch that day.</summary>
    public static DaySegment? On(AgendaEntry entry, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (day < entry.FirstDay || day >= entry.LastDayExclusive)
        {
            return null;
        }

        var dayStart = day.StartOfDay(zone);
        var dayEnd = day.AddDays(1).StartOfDay(zone);
        var start = entry.Start < dayStart ? dayStart : entry.Start;
        var end = entry.End > dayEnd ? dayEnd : entry.End;
        if (end < start)
        {
            end = start;
        }

        return new DaySegment(entry, day, start, end, entry.Start < dayStart, entry.End > dayEnd);
    }

    /// <summary>All segments of <paramref name="entry"/>, one per day it touches.</summary>
    public static IEnumerable<DaySegment> Split(AgendaEntry entry, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        for (var day = entry.FirstDay; day < entry.LastDayExclusive; day = day.AddDays(1))
        {
            var segment = On(entry, day, zone);
            if (segment is not null)
            {
                yield return segment;
            }
        }
    }
}
