using System.Globalization;
using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Views;

/// <summary>Turns a recurrence into a short sentence such as "Every 2 weeks on Monday, Wednesday, 10 times".</summary>
public static class RecurrenceDescriber
{
    internal const string Custom = "Repeats (custom rule)";

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FREQ", "INTERVAL", "BYDAY", "BYMONTHDAY", "COUNT", "UNTIL", "WKST",
    };

    public static string Describe(Recurrence recurrence, CultureInfo? culture = null)
    {
        Guard.NotNull(recurrence);
        culture ??= CultureInfo.CurrentCulture;
        var main = string.IsNullOrWhiteSpace(recurrence.Rule)
            ? (recurrence.ExtraDates.Count > 0 ? "Repeats on chosen dates" : Custom)
            : DescribeRule(recurrence.Rule, culture);
        var extras = string.IsNullOrWhiteSpace(recurrence.Rule) ? [] : ExtraText(recurrence);
        return extras.Count == 0 ? main : $"{main} ({string.Join(", ", extras)})";
    }

    private static List<string> ExtraText(Recurrence recurrence)
    {
        var extras = new List<string>();
        if (recurrence.ExtraDates.Count > 0)
        {
            extras.Add($"{recurrence.ExtraDates.Count} extra {(recurrence.ExtraDates.Count == 1 ? "date" : "dates")}");
        }

        if (recurrence.ExcludedDates.Count > 0)
        {
            extras.Add($"{recurrence.ExcludedDates.Count} skipped");
        }

        return extras;
    }

    private static string DescribeRule(string rule, CultureInfo culture)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !KnownKeys.Contains(part[..eq]))
            {
                return Custom;
            }

            parts[part[..eq]] = part[(eq + 1)..];
        }

        if (!parts.TryGetValue("FREQ", out var freq))
        {
            return Custom;
        }

        var interval = 1;
        if (parts.TryGetValue("INTERVAL", out var intervalText)
            && (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1))
        {
            return Custom;
        }

        var unit = freq.ToUpperInvariant() switch
        {
            "DAILY" => "day",
            "WEEKLY" => "week",
            "MONTHLY" => "month",
            "YEARLY" => "year",
            _ => null,
        };
        if (unit is null)
        {
            return Custom;
        }

        var text = interval == 1 ? $"Every {unit}" : $"Every {interval} {unit}s";

        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            if (interval == 1 && unit is "day" or "week" && IsWeekdays(byDay))
            {
                text = "Every weekday";
            }
            else
            {
                var days = DescribeDays(byDay, culture);
                if (days is null)
                {
                    return Custom;
                }

                text += $" on {days}";
            }
        }

        if (parts.TryGetValue("BYMONTHDAY", out var byMonthDay))
        {
            var numbers = byMonthDay.Split(',', StringSplitOptions.TrimEntries);
            if (numbers.Any(n => !int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) || d is 0 or < -31 or > 31))
            {
                return Custom;
            }

            text += $" on day {string.Join(", ", numbers.Select(n => n == "-1" ? "last" : n))}";
        }

        if (parts.TryGetValue("COUNT", out var countText))
        {
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            {
                return Custom;
            }

            text += count == 1 ? ", once" : $", {count} times";
        }

        if (parts.TryGetValue("UNTIL", out var untilText))
        {
            if (!TryParseUntil(untilText, out var until))
            {
                return Custom;
            }

            text += $", until {until.ToString("d MMM yyyy", culture)}";
        }

        return text;
    }

    private static bool IsWeekdays(string byDay) =>
        byDay.Split(',', StringSplitOptions.TrimEntries).Select(d => d.ToUpperInvariant()).Order().SequenceEqual(["FR", "MO", "TH", "TU", "WE"]);

    // BYDAY entries are "MO" or with an ordinal such as "1MO" or "-1FR".
    private static string? DescribeDays(string byDay, CultureInfo culture)
    {
        var names = new List<string>();
        foreach (var token in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2)
            {
                return null;
            }

            var index = Array.IndexOf(DayCodes, token[^2..].ToUpperInvariant());
            var ordinalText = token[..^2];
            if (index < 0)
            {
                return null;
            }

            var day = culture.DateTimeFormat.GetDayName((DayOfWeek)index);
            if (ordinalText.Length == 0)
            {
                names.Add(day);
                continue;
            }

            if (!int.TryParse(ordinalText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ordinal))
            {
                return null;
            }

            var word = ordinal switch
            {
                1 => "first",
                2 => "second",
                3 => "third",
                4 => "fourth",
                5 => "fifth",
                -1 => "last",
                _ => null,
            };
            if (word is null)
            {
                return null;
            }

            names.Add($"the {word} {day}");
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static bool TryParseUntil(string text, out DateTime date)
    {
        string[] formats = ["yyyyMMdd", "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
