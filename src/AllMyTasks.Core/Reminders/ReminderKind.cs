namespace AllMyTasks.Core.Reminders;

/// <summary>What a reminder is about.</summary>
public enum ReminderKind
{
    /// <summary>A timed event is about to start.</summary>
    EventStart,

    /// <summary>A task is due.</summary>
    TaskDue,

    /// <summary>One notification standing in for reminders that were missed while the app was not running.</summary>
    MissedSummary,
}
