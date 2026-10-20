using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>A timed segment placed on a day column's time axis.</summary>
/// <param name="Segment">What to draw.</param>
/// <param name="StartMinute">Minutes of real elapsed time since the start of the day.</param>
/// <param name="EndMinute">Minutes since the start of the day; never less than <see cref="WeekLayout.MinDisplayMinutes"/> after the start.</param>
/// <param name="Column">Zero-based sub-column within its overlap cluster.</param>
/// <param name="ColumnCount">Number of sub-columns in its overlap cluster; width is 1 / this.</param>
public sealed record PositionedEntry(DaySegment Segment, int StartMinute, int EndMinute, int Column, int ColumnCount);

/// <summary>One day of the week grid.</summary>
/// <param name="Date">The day.</param>
/// <param name="IsToday">Whether it is today.</param>
/// <param name="TotalMinutes">Real length of the day: 1380 or 1500 on DST changes, otherwise 1440.</param>
/// <param name="Timed">Timed entries with their positions.</param>
public sealed record DayColumn(DateOnly Date, bool IsToday, int TotalMinutes, IReadOnlyList<PositionedEntry> Timed);

/// <summary>An all-day item drawn across one or more day columns in the header strip.</summary>
/// <param name="Entry">What to draw.</param>
/// <param name="StartColumn">Zero-based first column.</param>
/// <param name="ColumnSpan">Number of columns covered.</param>
/// <param name="Lane">Zero-based row within the strip.</param>
/// <param name="ContinuedFromBefore">Starts before the first visible column.</param>
/// <param name="ContinuesAfter">Ends after the last visible column.</param>
public sealed record SpanBar(AgendaEntry Entry, int StartColumn, int ColumnSpan, int Lane, bool ContinuedFromBefore, bool ContinuesAfter);

/// <summary>The week view, ready to render.</summary>
/// <param name="Days">Seven (or five) day columns.</param>
/// <param name="AllDaySpans">Header strip bars with lane numbers.</param>
/// <param name="VisibleStartHour">First hour row to show (working hours, widened to fit events).</param>
/// <param name="VisibleEndHour">Hour after the last row to show.</param>
public sealed record WeekLayout(
    IReadOnlyList<DayColumn> Days,
    IReadOnlyList<SpanBar> AllDaySpans,
    int VisibleStartHour,
    int VisibleEndHour)
{
    public const int MinDisplayMinutes = 15;
    public const int DefaultStartHour = 8;
    public const int DefaultEndHour = 20;

    public int AllDayLaneCount => AllDaySpans.Count == 0 ? 0 : AllDaySpans.Max(b => b.Lane) + 1;

    /// <summary>The first day of the week containing <paramref name="date"/>.</summary>
    public static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDay)
    {
        var diff = ((int)date.DayOfWeek - (int)firstDay + 7) % 7;
        return date.AddDays(-diff);
    }

    /// <summary>Builds the week starting on <paramref name="weekStart"/>.</summary>
    public static WeekLayout Build(
        DateOnly weekStart,
        IEnumerable<AgendaEntry> entries,
        DateTimeOffset now,
        TimeZoneInfo zone,
        bool hideWeekends = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var today = now.Today(zone);
        var dates = Enumerable.Range(0, 7).Select(weekStart.AddDays)
            .Where(d => !hideWeekends || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToList();

        var columns = new List<DayColumn>();
        var earliest = DefaultStartHour * 60;
        var latest = DefaultEndHour * 60;
        foreach (var date in dates)
        {
            var dayStart = date.StartOfDay(zone);
            var total = (int)(date.AddDays(1).StartOfDay(zone) - dayStart).TotalMinutes;
            var segments = list
                .Where(e => !e.IsAllDay)
                .Select(e => DaySegment.On(e, date, zone))
                .OfType<DaySegment>()
                .OrderBy(s => s.Start)
                .ThenByDescending(s => s.End)
                .ToList();
            var positioned = Pack(segments, dayStart, total);
            foreach (var p in positioned)
            {
                earliest = Math.Min(earliest, p.StartMinute);
                latest = Math.Max(latest, p.EndMinute);
            }

            columns.Add(new DayColumn(date, date == today, total, positioned));
        }

        var startHour = Math.Max(0, earliest / 60);
        var endHour = Math.Min(24, (latest + 59) / 60);
        return new WeekLayout(columns, PackSpans(list.Where(e => e.IsAllDay), dates), startHour, endHour);
    }

    private static List<PositionedEntry> Pack(List<DaySegment> segments, DateTimeOffset dayStart, int totalMinutes)
    {
        var result = new List<PositionedEntry>();
        var cluster = new List<(DaySegment Segment, int Start, int End, int Column)>();
        var columnEnds = new List<int>();
        var clusterEnd = int.MinValue;

        void Flush()
        {
            var count = columnEnds.Count;
            result.AddRange(cluster.Select(c => new PositionedEntry(c.Segment, c.Start, c.End, c.Column, count)));
            cluster.Clear();
            columnEnds.Clear();
            clusterEnd = int.MinValue;
        }

        foreach (var segment in segments)
        {
            var start = Math.Clamp((int)Math.Floor((segment.Start - dayStart).TotalMinutes), 0, totalMinutes);
            var end = Math.Clamp((int)Math.Ceiling((segment.End - dayStart).TotalMinutes), 0, totalMinutes);
            end = Math.Max(end, Math.Min(start + MinDisplayMinutes, totalMinutes));

            if (cluster.Count > 0 && start >= clusterEnd)
            {
                Flush();
            }

            var column = columnEnds.FindIndex(e => e <= start);
            if (column < 0)
            {
                columnEnds.Add(end);
                column = columnEnds.Count - 1;
            }
            else
            {
                columnEnds[column] = end;
            }

            cluster.Add((segment, start, end, column));
            clusterEnd = Math.Max(clusterEnd, end);
        }

        Flush();
        return result;
    }

    private static List<SpanBar> PackSpans(IEnumerable<AgendaEntry> allDay, List<DateOnly> dates)
    {
        var bars = new List<(AgendaEntry Entry, int First, int Last, bool Before, bool After)>();
        foreach (var entry in allDay)
        {
            var covered = dates.Select((d, i) => (Date: d, Index: i)).Where(x => x.Date >= entry.FirstDay && x.Date < entry.LastDayExclusive).ToList();
            if (covered.Count == 0)
            {
                continue;
            }

            bars.Add((entry, covered[0].Index, covered[^1].Index, entry.FirstDay < dates[0], entry.LastDayExclusive > dates[^1].AddDays(1)));
        }

        return LanePacker.Pack(bars, b => b.First, b => b.Last)
            .Select(p => new SpanBar(p.Item.Entry, p.Item.First, p.Item.Last - p.Item.First + 1, p.Lane, p.Item.Before, p.Item.After))
            .ToList();
    }
}

internal static class LanePacker
{
    /// <summary>Assigns each item the first lane that is free over its column range. Longer items go first so bars stay tidy.</summary>
    public static IEnumerable<(T Item, int Lane)> Pack<T>(IEnumerable<T> items, Func<T, int> first, Func<T, int> last)
    {
        var laneEnds = new List<int>();
        foreach (var item in items.OrderBy(first).ThenByDescending(i => last(i) - first(i)))
        {
            var lane = laneEnds.FindIndex(end => end < first(item));
            if (lane < 0)
            {
                laneEnds.Add(last(item));
                lane = laneEnds.Count - 1;
            }
            else
            {
                laneEnds[lane] = last(item);
            }

            yield return (item, lane);
        }
    }
}
