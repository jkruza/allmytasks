using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Reminders;

/// <summary>Identifies one reminder: an item and the occurrence start or due time it is for.</summary>
/// <param name="Key">The event or task. For a recurring series this is the master (or override) key.</param>
/// <param name="Occurrence">Start of the event occurrence, or the due instant of the task (the planned time for a date-only task).</param>
public sealed record ReminderId(ItemKey Key, DateTimeOffset Occurrence);

/// <summary>A notification to show at <see cref="FireAt"/>.</summary>
/// <param name="Key">The event or task the reminder is about.</param>
/// <param name="FireAt">When it should appear.</param>
/// <param name="Kind">Event start, task due, or the missed summary.</param>
/// <param name="Title">Plain text, already cleaned and shortened.</param>
/// <param name="Detail">Plain text such as the start time and location, already cleaned and shortened.</param>
/// <param name="Occurrence">Start of the occurrence or due time; with <paramref name="Key"/> it identifies the reminder.</param>
/// <param name="EndsAt">When the reminder stops being relevant: the end of the event, or a day after a task is due.</param>
public sealed record Reminder(
    ItemKey Key,
    DateTimeOffset FireAt,
    ReminderKind Kind,
    string Title,
    string? Detail,
    DateTimeOffset Occurrence,
    DateTimeOffset EndsAt)
{
    public ReminderId Id => new(Key, Occurrence);

    /// <summary>The summary notification has nothing to snooze.</summary>
    public bool CanSnooze => Kind != ReminderKind.MissedSummary;

    /// <summary>The notification for <paramref name="count"/> reminders that were missed.</summary>
    public static Reminder Missed(int count, DateTimeOffset now) => new(
        new ItemKey("reminders", "summary", "missed"),
        now,
        ReminderKind.MissedSummary,
        count == 1 ? "1 reminder missed" : $"{count} reminders missed",
        "They were due while All My Tasks was not running.",
        now,
        now);
}
