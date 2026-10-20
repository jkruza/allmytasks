using System.Globalization;

namespace AllMyTasks.Core.Views;

/// <summary>Short "how long ago" text for status lines.</summary>
public static class RelativeTime
{
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var age = now - then;
        if (age < TimeSpan.Zero || age < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(60))
        {
            var minutes = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes ago");
        }

        if (age < TimeSpan.FromHours(24))
        {
            var hours = (int)Math.Round(age.TotalHours);
            return hours == 1 ? "1 hour ago" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours ago");
        }

        var days = (int)Math.Floor(age.TotalDays);
        return days == 1 ? "yesterday" : string.Create(CultureInfo.InvariantCulture, $"{days} days ago");
    }
}
