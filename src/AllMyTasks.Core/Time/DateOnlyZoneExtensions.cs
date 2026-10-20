namespace AllMyTasks.Core.Time;

internal static class DateOnlyZoneExtensions
{
    /// <summary>
    /// The first instant of <paramref name="date"/> in <paramref name="zone"/>. If midnight does not
    /// exist that day (a DST gap at midnight), the first valid local time is used.
    /// </summary>
    public static DateTimeOffset StartOfDay(this DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
