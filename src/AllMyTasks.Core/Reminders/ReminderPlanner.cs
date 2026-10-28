using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Reminders;

/// <summary>
/// Works out which reminders fall in a time window. A pure function of the agenda entries (which already carry
/// expanded recurrences and leave out hidden collections), the settings and the display zone, so DST and
/// recurrence behaviour is tested without a timer. See docs/design/reminders.md.
/// </summary>
public static class ReminderPlanner
{
    /// <summary>
    /// Returns the reminders whose <see cref="Reminder.FireAt"/> lies in [<paramref name="from"/>, <paramref name="to"/>],
    /// earliest first. All-day events, cancelled events and tasks that are not open produce none.
    /// </summary>
    public static IReadOnlyList<Reminder> Plan(
        IEnumerable<AgendaEntry> entries,
        ReminderSettings settings,
        TimeZoneInfo displayZone,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(displayZone);
        if (!settings.Enabled)
        {
            return [];
        }

        var result = new List<Reminder>();
        foreach (var entry in entries)
        {
            var reminder = entry switch
            {
                EventEntry e => ForEvent(e, settings, displayZone),
                TaskEntry t when settings.Tasks => ForTask(t, settings, displayZone),
                _ => null,
            };
            if (reminder is not null && reminder.FireAt >= from && reminder.FireAt <= to)
            {
                result.Add(reminder);
            }
        }

        return [.. result.OrderBy(r => r.FireAt).ThenBy(r => r.Title, StringComparer.Ordinal)];
    }

    private static Reminder? ForEvent(EventEntry entry, ReminderSettings settings, TimeZoneInfo zone)
    {
        if (entry.IsAllDay || entry.Occurrence.Event.Status == EventStatus.Cancelled)
        {
            return null;
        }

        var start = entry.Start;
        var detail = new List<string> { TimeZoneInfo.ConvertTime(start, zone).ToString("t", CultureInfo.CurrentCulture) };
        if (!string.IsNullOrWhiteSpace(entry.Location))
        {
            detail.Add(entry.Location);
        }

        return new Reminder(
            entry.Key,
            start - settings.EventLead,
            ReminderKind.EventStart,
            Title(entry.Title),
            ReminderText.Clean(string.Join(" · ", detail), ReminderText.MaxDetailLength),
            start,
            // A zero-length event still counts as relevant for a moment, or a reminder at its start would be stale at once.
            entry.End > start ? entry.End : start + TimeSpan.FromMinutes(1));
    }

    private static Reminder? ForTask(TaskEntry entry, ReminderSettings settings, TimeZoneInfo zone)
    {
        if (!entry.Task.IsOpen)
        {
            return null;
        }

        DateTimeOffset at;
        switch (entry.Task.Due)
        {
            case DueDateTime due:
                at = due.At;
                break;
            case DueDate date:
                at = AtTimeOfDay(date.Date, settings.DateOnlyTaskTime, zone);
                break;
            default:
                return null;
        }

        return new Reminder(
            entry.Key,
            at,
            ReminderKind.TaskDue,
            Title(entry.Title),
            ReminderText.Clean($"Due · {entry.CollectionName}", ReminderText.MaxDetailLength),
            at,
            at.AddDays(1));
    }

    private static string Title(string title)
    {
        var clean = ReminderText.Clean(title, ReminderText.MaxTitleLength);
        return clean.Length == 0 ? "(no title)" : clean;
    }

    /// <summary>Local <paramref name="time"/> on <paramref name="date"/>: later if it falls in a DST gap, the earlier instant if it happens twice.</summary>
    internal static DateTimeOffset AtTimeOfDay(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
