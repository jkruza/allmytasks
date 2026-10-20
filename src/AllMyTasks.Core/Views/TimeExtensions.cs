namespace AllMyTasks.Core.Views;

internal static class TimeExtensions
{
    public static DateOnly Today(this DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
}
