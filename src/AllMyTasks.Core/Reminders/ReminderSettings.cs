namespace AllMyTasks.Core.Reminders;

/// <summary>How reminders behave. Derived from <see cref="Settings.AppSettings"/>.</summary>
/// <param name="Enabled">The master switch.</param>
/// <param name="EventLead">How long before an event starts the reminder appears.</param>
/// <param name="Tasks">Remind about tasks at all.</param>
/// <param name="DateOnlyTaskTime">Time of day, in the display zone, for tasks that have a date but no time.</param>
/// <param name="SnoozeLength">How long Snooze waits.</param>
public sealed record ReminderSettings(bool Enabled, TimeSpan EventLead, bool Tasks, TimeOnly DateOnlyTaskTime, TimeSpan SnoozeLength)
{
    public static ReminderSettings Default { get; } = new(true, TimeSpan.FromMinutes(10), true, new TimeOnly(9, 0), TimeSpan.FromMinutes(10));
}
