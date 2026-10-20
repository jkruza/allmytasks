using System.Globalization;
using AllMyTasks.Core.Calendar;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>
/// Works out the last moment a recurring series can reach, so range queries can skip finished series. Returns
/// null (unbounded) when it cannot be sure, for example when the rule only has <c>COUNT</c> or no end.
/// </summary>
internal static class RecurrenceBounds
{
    public static long? UntilUtcMilliseconds(CalendarEvent master, TimeSpan duration)
    {
        var recurrence = master.Recurrence;
        if (recurrence is null)
        {
            return null;
        }

        var last = master.Time is TimedEvent t ? t.End : new DateTimeOffset(((AllDayEvent)master.Time).EndExclusive.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        foreach (var extra in recurrence.ExtraDates)
        {
            var end = extra + duration;
            if (end > last)
            {
                last = end;
            }
        }

        var hasRule = !string.IsNullOrWhiteSpace(recurrence.Rule);
        if (!hasRule)
        {
            return last.ToUnixTimeMilliseconds();
        }

        var until = ParseUntil(recurrence.Rule);
        if (until is null)
        {
            return null;
        }

        var untilEnd = until.Value + duration;
        return (untilEnd > last ? untilEnd : last).ToUnixTimeMilliseconds();
    }

    internal static DateTimeOffset? ParseUntil(string rule)
    {
        foreach (var part in rule.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[6..];
            string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];
            if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                // A date-only UNTIL covers that whole day.
                return value.Length == 8 ? parsed.AddDays(1) : parsed;
            }
        }

        return null;
    }
}
