using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Settings;

/// <summary>One choice for the app accent. <paramref name="Hex"/> is null for the operating system's accent.</summary>
public sealed record AccentOption(string Id, string Label, string? Hex);

/// <summary>The accent choices of the design system and how a stored choice resolves to a colour.</summary>
public static class AccentColors
{
    /// <summary>Id of the default accent.</summary>
    public const string Blue = "blue";

    /// <summary>Id of the choice that follows the operating system.</summary>
    public const string System = "system";

    public const string BlueHex = "#3b82f6";

    public static IReadOnlyList<AccentOption> Options { get; } =
    [
        new(Blue, "Blue", BlueHex),
        new(System, "System accent", null),
        new("green", "Green", "#10b981"),
        new("amber", "Amber", "#f59e0b"),
        new("purple", "Purple", "#8b5cf6"),
        new("teal", "Teal", "#14b8a6"),
        new("pink", "Pink", "#ec4899"),
        new("red", "Red", "#ef4444"),
        new("gray", "Gray", "#6b7280"),
    ];

    /// <summary>The id itself when it is a known choice, otherwise <see cref="Blue"/>.</summary>
    public static string Normalize(string? id) =>
        Options.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)) ? id!.ToLowerInvariant() : Blue;

    /// <summary>
    /// The <c>#rrggbb</c> colour for a stored choice. The system choice uses <paramref name="systemHex"/> when it is a
    /// valid colour and blue when the platform gives none (some Linux desktops).
    /// </summary>
    public static string Resolve(string? id, string? systemHex)
    {
        var option = Options.First(o => o.Id == Normalize(id));
        if (option.Hex is not null)
        {
            return option.Hex;
        }

        return ColorUtil.TryParseHex(systemHex, out _) ? ColorUtil.OrDefault(systemHex) : BlueHex;
    }
}
