using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Views;

/// <summary>
/// One thing to show on a calendar, whether it started as an event occurrence or a dated task.
/// Everything is already resolved into the display zone, so views never touch providers or raw times.
/// </summary>
/// <param name="Key">Identity of the event (or occurrence) or task.</param>
/// <param name="Title">Title as stored; the view substitutes a placeholder for empty ones.</param>
/// <param name="Color">Collection colour (<c>#RRGGBB</c>), if any.</param>
/// <param name="CollectionName">Name of the collection it came from.</param>
/// <param name="Start">First instant. For all-day items, the start of the first day.</param>
/// <param name="End">End instant (exclusive). Equals <paramref name="Start"/> for points in time.</param>
/// <param name="IsAllDay">All-day events and date-only tasks.</param>
/// <param name="FirstDay">First day covered, in the display zone.</param>
/// <param name="LastDayExclusive">Day after the last day covered.</param>
public abstract record AgendaEntry(
    ItemKey Key,
    string Title,
    string? Color,
    string CollectionName,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    DateOnly FirstDay,
    DateOnly LastDayExclusive)
{
    /// <summary>True when the item covers more than one calendar day.</summary>
    public bool IsMultiDay => LastDayExclusive.DayNumber - FirstDay.DayNumber > 1;

    /// <summary>A single sentence for screen readers.</summary>
    public abstract string SpokenText(TimeZoneInfo zone);
}

/// <summary>An occurrence of a calendar event.</summary>
public sealed record EventEntry(
    EventOccurrence Occurrence,
    string? Color,
    string CollectionName,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    DateOnly FirstDay,
    DateOnly LastDayExclusive)
    : AgendaEntry(Occurrence.Event.Key, Occurrence.Event.Title, Color, CollectionName, Start, End, IsAllDay, FirstDay, LastDayExclusive)
{
    public string? Location => Occurrence.Event.Location;

    public bool IsTentative => Occurrence.Event.Status == EventStatus.Tentative;

    public bool HasMeeting => Occurrence.Event.MeetingUrl is not null;

    public override string SpokenText(TimeZoneInfo zone) =>
        $"{Title}, {AgendaText.TimeRange(this, zone)}, {CollectionName}{(HasMeeting ? ", online meeting" : string.Empty)}";
}

/// <summary>A task with a due date.</summary>
public sealed record TaskEntry(
    TaskItem Task,
    string? Color,
    string CollectionName,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    DateOnly FirstDay,
    DateOnly LastDayExclusive,
    bool IsOverdue)
    : AgendaEntry(Task.Key, Task.Title, Color, CollectionName, Start, End, IsAllDay, FirstDay, LastDayExclusive)
{
    public override string SpokenText(TimeZoneInfo zone) =>
        $"Task {Title}, {(IsOverdue ? "overdue, " : string.Empty)}{AgendaText.TimeRange(this, zone)}, {CollectionName}";
}

internal static class AgendaText
{
    public static string TimeRange(AgendaEntry entry, TimeZoneInfo zone)
    {
        if (entry.IsAllDay)
        {
            return "all day";
        }

        var start = TimeZoneInfo.ConvertTime(entry.Start, zone);
        var end = TimeZoneInfo.ConvertTime(entry.End, zone);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return entry.End == entry.Start
            ? start.ToString("t", culture)
            : $"{start.ToString("t", culture)} to {end.ToString("t", culture)}";
    }
}
