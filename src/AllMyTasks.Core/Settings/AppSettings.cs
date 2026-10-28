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
        });

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
}
