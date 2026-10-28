using System.Globalization;
using System.Text.Json;

namespace AllMyTasks.Core.Settings;

/// <summary>App-wide preferences edited in the Settings dialog. Add a property here to add a setting.</summary>
public sealed record AppSettings
{
    /// <summary>Key in the settings store.</summary>
    public const string StoreKey = "settings.app";

    public WeekStartSetting WeekStart { get; init; } = WeekStartSetting.System;

    /// <summary>Closing the main window hides it to the system tray instead of quitting.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Start All My Tasks when the user logs in to the OS.</summary>
    public bool LaunchAtLogin { get; init; }

    /// <summary>The login launch starts with only the tray icon (<c>--tray</c>). Regular launches are not affected.</summary>
    public bool StartInTrayAtLogin { get; init; }

    /// <summary>Show reminders for events and tasks while the app is running.</summary>
    public bool RemindersEnabled { get; init; } = true;

    /// <summary>Minutes before an event starts that its reminder appears. 0 means at the start.</summary>
    public int EventReminderMinutes { get; init; } = 10;

    /// <summary>Remind about tasks when they are due.</summary>
    public bool RemindAboutTasks { get; init; } = true;

    /// <summary>Time of day, in the display zone, at which tasks that have a date but no time are reminded.</summary>
    public TimeOnly DateOnlyTaskReminderTime { get; init; } = new(9, 0);

    /// <summary>Minutes a snoozed reminder waits.</summary>
    public int SnoozeMinutes { get; init; } = 10;

    /// <summary>Reads settings from JSON. Missing, unknown or corrupt values give the defaults.</summary>
    public static AppSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new AppSettings();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            if (root.TryGetProperty("weekStart", out var week)
                && week.ValueKind == JsonValueKind.String
                && Enum.TryParse<WeekStartSetting>(week.GetString(), ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed))
            {
                result = result with { WeekStart = parsed };
            }

            if (root.TryGetProperty("closeToTray", out var tray) && tray.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                result = result with { CloseToTray = tray.GetBoolean() };
            }

            result = result with
            {
                LaunchAtLogin = ReadBool(root, "launchAtLogin", result.LaunchAtLogin),
                StartInTrayAtLogin = ReadBool(root, "startInTrayAtLogin", result.StartInTrayAtLogin),
                RemindersEnabled = ReadBool(root, "remindersEnabled", result.RemindersEnabled),
                EventReminderMinutes = ReadMinutes(root, "eventReminderMinutes", result.EventReminderMinutes, 0),
                RemindAboutTasks = ReadBool(root, "remindAboutTasks", result.RemindAboutTasks),
                DateOnlyTaskReminderTime = ReadTime(root, "dateOnlyTaskReminderTime", result.DateOnlyTaskReminderTime),
                SnoozeMinutes = ReadMinutes(root, "snoozeMinutes", result.SnoozeMinutes, 1),
            };

            return result;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["weekStart"] = WeekStart.ToString(),
            ["closeToTray"] = CloseToTray,
            ["launchAtLogin"] = LaunchAtLogin,
            ["startInTrayAtLogin"] = StartInTrayAtLogin,
            ["remindersEnabled"] = RemindersEnabled,
            ["eventReminderMinutes"] = EventReminderMinutes,
            ["remindAboutTasks"] = RemindAboutTasks,
            ["dateOnlyTaskReminderTime"] = DateOnlyTaskReminderTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["snoozeMinutes"] = SnoozeMinutes,
        });

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static int ReadMinutes(JsonElement root, string name, int fallback, int minimum) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes) && minutes >= minimum && minutes <= 24 * 60
            ? minutes
            : fallback;

    private static TimeOnly ReadTime(JsonElement root, string name, TimeOnly fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && TimeOnly.TryParseExact(value.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : fallback;
}
