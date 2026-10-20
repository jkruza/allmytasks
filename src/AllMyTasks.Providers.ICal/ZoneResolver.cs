namespace AllMyTasks.Providers.ICal;

/// <summary>Maps the time zone names found in iCalendar files to .NET zones and IANA ids.</summary>
internal static class ZoneResolver
{
    /// <summary>
    /// Resolves an IANA id, a Windows id (<c>W. Europe Standard Time</c>) or an Outlook-style quoted/prefixed name.
    /// Returns null when unknown; callers fall back to UTC and warn.
    /// </summary>
    public static (TimeZoneInfo Zone, string IanaId)? Resolve(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid))
        {
            return null;
        }

        var name = tzid.Trim().Trim('"');

        // Some producers prefix the id, for example "/mozilla.org/20050126_1/Europe/Warsaw".
        var candidates = new List<string> { name };
        if (name.StartsWith('/') && name.Split('/') is { Length: >= 3 } parts)
        {
            candidates.Add(string.Join('/', parts[^2..]));
            candidates.Add(parts[^1]);
        }

        foreach (var candidate in candidates)
        {
            if (TryFind(candidate, out var zone))
            {
                return (zone, ToIana(zone));
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var iana) && TryFind(iana, out zone))
            {
                return (zone, iana);
            }
        }

        return null;
    }

    private static string ToIana(TimeZoneInfo zone) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

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
