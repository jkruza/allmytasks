using System.Text.Json;

namespace AllMyTasks.Core.Settings;

/// <summary>App-wide preferences edited in the Settings dialog. Add a property here to add a setting.</summary>
public sealed record AppSettings
{
    /// <summary>Key in the settings store.</summary>
    public const string StoreKey = "settings.app";

    public WeekStartSetting WeekStart { get; init; } = WeekStartSetting.System;

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
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("weekStart", out var week)
                && week.ValueKind == JsonValueKind.String
                && Enum.TryParse<WeekStartSetting>(week.GetString(), ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed))
            {
                result = result with { WeekStart = parsed };
            }

            return result;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, string> { ["weekStart"] = WeekStart.ToString() });
}
