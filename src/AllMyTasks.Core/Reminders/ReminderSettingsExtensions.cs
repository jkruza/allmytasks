using AllMyTasks.Core.Settings;

namespace AllMyTasks.Core.Reminders;

public static class ReminderSettingsExtensions
{
    /// <summary>The reminder behaviour the user chose in the Settings dialog.</summary>
    public static ReminderSettings ToReminderSettings(this AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ReminderSettings(
            settings.RemindersEnabled,
            TimeSpan.FromMinutes(settings.EventReminderMinutes),
            settings.RemindAboutTasks,
            settings.DateOnlyTaskReminderTime,
            TimeSpan.FromMinutes(settings.SnoozeMinutes));
    }
}
