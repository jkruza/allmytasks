using System.Globalization;

namespace AllMyTasks.Core.Views;

/// <summary>Colour helpers so chips stay readable on any collection colour, in light, dark and high-contrast themes.</summary>
public static class ColorUtil
{
    public const string DefaultColor = "#6b7280";

    /// <summary>Parses <c>#RRGGBB</c> (or <c>RRGGBB</c>). Returns false for anything else.</summary>
    public static bool TryParseHex(string? text, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 }
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        rgb = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance((byte R, byte G, byte B) rgb)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(rgb.R)) + (0.7152 * Channel(rgb.G)) + (0.0722 * Channel(rgb.B));
    }

    /// <summary>WCAG contrast ratio between two colours, 1 to 21.</summary>
    public static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>. Unparseable input falls back to the default colour.</summary>
    public static string ContrastingText(string? background)
    {
        var rgb = TryParseHex(background, out var parsed) ? parsed : DefaultRgb;
        return Contrast(rgb, (0, 0, 0)) >= Contrast(rgb, (255, 255, 255)) ? "#000000" : "#FFFFFF";
    }

    /// <summary>The colour to use for a collection: its own if valid, otherwise a neutral grey.</summary>
    public static string OrDefault(string? color) => TryParseHex(color, out _) ? "#" + color!.Trim().TrimStart('#') : DefaultColor;

    private static readonly (byte R, byte G, byte B) DefaultRgb = (0x6b, 0x72, 0x80);
}
