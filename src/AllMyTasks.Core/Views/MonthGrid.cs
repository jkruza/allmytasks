using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Views;

/// <summary>One day cell of the month grid.</summary>
/// <param name="Date">The day.</param>
/// <param name="IsCurrentMonth">False for the dimmed leading and trailing days.</param>
/// <param name="IsToday">Whether it is today.</param>
/// <param name="Items">Timed events and tasks on that day, in time order. All-day events are drawn as bars instead.</param>
public sealed record DayCell(DateOnly Date, bool IsCurrentMonth, bool IsToday, IReadOnlyList<DaySegment> Items);

/// <summary>An all-day or multi-day event drawn across cells of one week row.</summary>
/// <param name="Entry">What to draw.</param>
/// <param name="StartColumn">Zero-based first column.</param>
/// <param name="ColumnSpan">Columns covered within this row.</param>
/// <param name="Lane">Zero-based lane within the row.</param>
/// <param name="ContinuedFromBefore">Started in an earlier row.</param>
/// <param name="ContinuesAfter">Goes on into the next row.</param>
public sealed record MonthSpanBar(AgendaEntry Entry, int StartColumn, int ColumnSpan, int Lane, bool ContinuedFromBefore, bool ContinuesAfter);

/// <summary>One row (week) of the month grid.</summary>
public sealed record WeekRow(IReadOnlyList<DayCell> Days, IReadOnlyList<MonthSpanBar> SpanBars)
{
    public int LaneCount => SpanBars.Count == 0 ? 0 : SpanBars.Max(b => b.Lane) + 1;
}

/// <summary>A month as always six rows of seven days, so the layout does not jump between months.</summary>
/// <param name="Month">The first day of the month shown.</param>
/// <param name="Rows">Six week rows.</param>
public sealed record MonthGrid(DateOnly Month, IReadOnlyList<WeekRow> Rows)
{
    public const int RowCount = 6;

    /// <summary>The first day of the first row: the start of the week containing the 1st of the month.</summary>
    public static DateOnly GridStart(DateOnly month, DayOfWeek firstDay) =>
        WeekLayout.StartOfWeek(new DateOnly(month.Year, month.Month, 1), firstDay);

    public static MonthGrid Build(
        DateOnly month,
        IEnumerable<AgendaEntry> entries,
        DateTimeOffset now,
        TimeZoneInfo zone,
        DayOfWeek firstDay)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var first = new DateOnly(month.Year, month.Month, 1);
        var list = entries.ToList();
        var today = now.Today(zone);
        var gridStart = GridStart(first, firstDay);
        var rows = new List<WeekRow>();

        for (var r = 0; r < RowCount; r++)
        {
            var dates = Enumerable.Range(0, 7).Select(i => gridStart.AddDays((r * 7) + i)).ToList();
            var cells = dates.Select(d => new DayCell(
                d,
                d.Month == first.Month && d.Year == first.Year,
                d == today,
                [.. list
                    .Where(e => !IsBar(e))
                    .Select(e => DaySegment.On(e, d, zone))
                    .OfType<DaySegment>()
                    .OrderBy(s => s.Start)
                    .ThenBy(s => s.Entry.Title, StringComparer.CurrentCultureIgnoreCase)])).ToList();

            var bars = new List<(AgendaEntry Entry, int First, int Last, bool Before, bool After)>();
            foreach (var entry in list.Where(IsBar))
            {
                var covered = dates.Select((d, i) => (Date: d, Index: i)).Where(x => x.Date >= entry.FirstDay && x.Date < entry.LastDayExclusive).ToList();
                if (covered.Count > 0)
                {
                    bars.Add((entry, covered[0].Index, covered[^1].Index, entry.FirstDay < dates[0], entry.LastDayExclusive > dates[6].AddDays(1)));
                }
            }

            var spans = LanePacker.Pack(bars, b => b.First, b => b.Last)
                .Select(p => new MonthSpanBar(p.Item.Entry, p.Item.First, p.Item.Last - p.Item.First + 1, p.Lane, p.Item.Before, p.Item.After))
                .ToList();
            rows.Add(new WeekRow(cells, spans));
        }

        return new MonthGrid(first, rows);
    }

    // All-day events are bars; date-only tasks are small markers inside their cell.
    private static bool IsBar(AgendaEntry entry) => entry is EventEntry { IsAllDay: true };
}
