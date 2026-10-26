namespace AllMyTasks.Providers.Microsoft;

/// <summary>Maps the zone names Graph uses (Windows ids, IANA ids, <c>UTC</c>) to .NET zones.</summary>
internal static class Zones
{
    /// <summary>Resolves a Windows or IANA zone name; null when unknown or blank.</summary>
    public static (TimeZoneInfo Zone, string IanaId)? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "tzone://Microsoft/Utc", StringComparison.OrdinalIgnoreCase))
        {
            return (TimeZoneInfo.Utc, "UTC");
        }

        if (TryFind(trimmed, out var zone))
        {
            return (zone, TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) ? converted : zone.Id);
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var iana) && TryFind(iana, out zone))
        {
            return (zone, iana);
        }

        return null;
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

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
