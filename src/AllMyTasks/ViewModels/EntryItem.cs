using System.Globalization;
using AllMyTasks.Core.Views;
using Avalonia.Media;

namespace AllMyTasks.ViewModels;

/// <summary>Placement of a bar across day columns (week header strip, month rows).</summary>
public interface ISpanPlacement
{
    int StartColumn { get; }

    int ColumnSpan { get; }

    int Lane { get; }
}

/// <summary>Placement of a block on a day's time axis.</summary>
public interface ITimelinePlacement
{
    int StartMinute { get; }

    int EndMinute { get; }

    int Column { get; }

    int ColumnCount { get; }
}

/// <summary>An event or task prepared for display: text, colours and a spoken description.</summary>
public class EntryItem
{
    public EntryItem(DaySegment segment, DateTimeOffset now, TimeZoneInfo zone)
    {
        var entry = segment.Entry;
        Entry = entry;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "(no title)" : entry.Title;
        TimeText = FormatTime(segment, zone);
        Location = (entry as EventEntry)?.Location;
        IsTask = entry is TaskEntry;
        IsOverdue = entry is TaskEntry { IsOverdue: true };
        IsPast = segment.IsPast(now) && !IsOverdue;
        IsCompleted = entry is TaskEntry { Task.IsCompleted: true };
        IsTentative = entry is EventEntry { IsTentative: true };
        var color = ColorUtil.OrDefault(entry.Color);
        Accent = Brush.Parse(color);
        ChipBackground = Brush.Parse(color);
        ChipForeground = Brush.Parse(ColorUtil.ContrastingText(color));
        Glyph = !IsTask ? string.Empty : IsCompleted ? "☑" : "☐";
        CollectionName = entry.CollectionName;
        Spoken = entry.SpokenText(zone);
        Opacity = IsPast || IsCompleted ? 0.7 : 1;
    }

    public AgendaEntry Entry { get; }

    public string Title { get; }

    public string TimeText { get; }

    public string? Location { get; }

    public string CollectionName { get; }

    public bool IsTask { get; }

    public bool IsOverdue { get; }

    public bool IsPast { get; }

    public bool IsCompleted { get; }

    public bool IsTentative { get; }

    public string Glyph { get; }

    public IBrush Accent { get; }

    public IBrush ChipBackground { get; }

    public IBrush ChipForeground { get; }

    public string Spoken { get; }

    public double Opacity { get; }

    public bool HasLocation => !string.IsNullOrEmpty(Location);

    public static string FormatTime(DaySegment segment, TimeZoneInfo zone)
    {
        if (segment.IsAllDay)
        {
            return "All day";
        }

        var culture = CultureInfo.CurrentCulture;
        var start = TimeZoneInfo.ConvertTime(segment.Start, zone).ToString("t", culture);
        if (segment.End == segment.Start)
        {
            return start;
        }

        var end = TimeZoneInfo.ConvertTime(segment.End, zone);
        var endText = segment.ContinuesAfter ? "…" : end.ToString("t", culture);
        return segment.ContinuedFromBefore ? $"… {endText}" : $"{start} – {endText}";
    }
}

/// <summary>An entry on a week column's time axis.</summary>
public sealed class TimedEntryItem(PositionedEntry positioned, DateTimeOffset now, TimeZoneInfo zone)
    : EntryItem(positioned.Segment, now, zone), ITimelinePlacement
{
    public int StartMinute { get; } = positioned.StartMinute;

    public int EndMinute { get; } = positioned.EndMinute;

    public int Column { get; } = positioned.Column;

    public int ColumnCount { get; } = positioned.ColumnCount;
}

/// <summary>An all-day bar spanning day columns.</summary>
public sealed class SpanBarItem : EntryItem, ISpanPlacement
{
    public SpanBarItem(AgendaEntry entry, int startColumn, int columnSpan, int lane, bool fromBefore, bool continues, DateTimeOffset now, TimeZoneInfo zone)
        : base(
            new DaySegment(entry, entry.FirstDay, entry.Start, entry.End, fromBefore, continues),
            now,
            zone)
    {
        StartColumn = startColumn;
        ColumnSpan = columnSpan;
        Lane = lane;
        Prefix = fromBefore ? "◀ " : string.Empty;
        Suffix = continues ? " ▶" : string.Empty;
    }

    public int StartColumn { get; }

    public int ColumnSpan { get; }

    public int Lane { get; }

    public string Prefix { get; }

    public string Suffix { get; }

    public string BarText => Prefix + Title + Suffix;
}
