namespace AllMyTasks.Providers.Google;

/// <summary>Resolves the IANA zone names Google uses.</summary>
internal static class Zones
{
    /// <summary>The zone with this id, or null when it is blank or unknown.</summary>
    public static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The instant of a wall-clock time in a zone. A time that does not exist (a DST gap) moves forward to the first valid one.</summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
