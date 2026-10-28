using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>Maps Graph calendars and events to the app's model. Pure: no I/O.</summary>
internal sealed class CalendarMapper(string sourceId)
{
    public CollectionInfo? MapCalendar(CalendarDto calendar) =>
        calendar.Id is { Length: > 0 } id
            ? new CollectionInfo(sourceId, id, calendar.Name ?? string.Empty, CollectionKind.Calendar, ColorFor(calendar.HexColor), IsVisible: true, IsReadOnly: true)
            : null;

    internal static string? ColorFor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;

    /// <summary>Maps one event. Returns null (with a warning) when it cannot be read; softer problems add a warning and keep the event.</summary>
    public CalendarEvent? MapEvent(string collectionId, EventDto dto, List<SyncWarning> warnings)
    {
        if (string.IsNullOrEmpty(dto.Id))
        {
            return null;
        }

        var originalZone = Zones.Resolve(dto.OriginalStartTimeZone);
        if (originalZone is null && !string.IsNullOrWhiteSpace(dto.OriginalStartTimeZone))
        {
            warnings.Add(new SyncWarning(dto.Id, "An event has a time zone this version does not know."));
        }

        if (!TryInstant(dto.Start, out var start) || !TryInstant(dto.End, out var end))
        {
            warnings.Add(new SyncWarning(dto.Id, "An event has a start or end that could not be read and was skipped."));
            return null;
        }

        if (end < start)
        {
            end = start;
            warnings.Add(new SyncWarning(dto.Id, "An event ended before it started; it was shown as a point in time."));
        }

        EventTime time = dto.IsAllDay
            ? AllDay(dto.Id, start, end, originalZone?.Zone ?? TimeZoneInfo.Utc, warnings)
            : new TimedEvent(start, end, originalZone?.IanaId);

        // Graph does not say which attendee is the account owner, so IsSelf stays false here.
        var attendees = Attendee.Cap(
            (dto.Attendees ?? []).Select(a => new Attendee(
                a.EmailAddress?.Name,
                a.EmailAddress?.Address,
                string.Equals(a.Type, "resource", StringComparison.OrdinalIgnoreCase) ? AttendeeRole.Resource : string.Equals(a.Type, "optional", StringComparison.OrdinalIgnoreCase) ? AttendeeRole.Optional : AttendeeRole.Required,
                ResponseOf(a.Status?.Response))),
            out var total);

        return new CalendarEvent(
            new ItemKey(sourceId, collectionId, dto.Id),
            dto.Subject ?? string.Empty,
            time,
            string.IsNullOrEmpty(dto.Body?.Content) ? null : dto.Body.Content,
            LocationText(dto),
            Text.HttpsUri(dto.WebLink),
            MapStatus(dto),
            string.Equals(dto.ShowAs, "free", StringComparison.OrdinalIgnoreCase) ? Availability.Free : Availability.Busy,
            Recurrence: null,
            Categories: dto.Categories,
            LastModified: Text.ParseInstant(dto.LastModifiedDateTime),
            MeetingUrl: MeetingLinks.Sanitize(dto.OnlineMeeting?.JoinUrl) ?? MeetingLinks.Sanitize(dto.OnlineMeetingUrl),
            Organizer: dto.Organizer?.EmailAddress is { } o ? new Attendee(o.Name, o.Address, AttendeeRole.Required, ResponseStatus.Accepted, dto.IsOrganizer) : null,
            Attendees: attendees,
            AttendeeCount: total);
    }

    private static ResponseStatus ResponseOf(string? response) => response?.ToLowerInvariant() switch
    {
        "accepted" or "organizer" => ResponseStatus.Accepted,
        "tentativelyaccepted" => ResponseStatus.Tentative,
        "declined" => ResponseStatus.Declined,
        _ => ResponseStatus.NeedsAction,
    };

    internal static EventStatus MapStatus(EventDto dto)
    {
        var response = dto.ResponseStatus?.Response;
        if (dto.IsCancelled || string.Equals(response, "declined", StringComparison.OrdinalIgnoreCase))
        {
            return EventStatus.Cancelled;
        }

        return string.Equals(dto.ShowAs, "tentative", StringComparison.OrdinalIgnoreCase) || string.Equals(response, "tentativelyAccepted", StringComparison.OrdinalIgnoreCase)
            ? EventStatus.Tentative
            : EventStatus.Confirmed;
    }

    private static string? LocationText(EventDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Location?.DisplayName))
        {
            return dto.Location.DisplayName;
        }

        var names = dto.Locations?.Select(l => l.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return names is { Count: > 0 } ? string.Join("; ", names) : null;
    }

    /// <summary>The instant of a <c>dateTimeTimeZone</c>: a value in UTC (what was asked for) or in another zone, which is converted.</summary>
    internal static bool TryInstant(DateTimeZoneDto? value, out DateTimeOffset instant)
    {
        instant = default;
        if (Text.ParseWallClock(value?.DateTime) is not { } wall)
        {
            return false;
        }

        if (Zones.Resolve(value!.TimeZone) is not { } zone)
        {
            return false;
        }

        instant = zone.Zone == TimeZoneInfo.Utc ? new DateTimeOffset(wall, TimeSpan.Zero) : Zones.ToInstant(wall, zone.Zone);
        return true;
    }

    /// <summary>
    /// An all-day event is midnight to midnight in the event's own zone. The instants are converted back to that zone and the dates
    /// taken; if a start is not at midnight there (a zone mismatch) the nearest date is used and a warning is added.
    /// </summary>
    private static AllDayEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, List<SyncWarning> warnings)
    {
        var first = DateOf(start, zone, out var startExact);
        var after = DateOf(end, zone, out var endExact);
        if (!startExact || !endExact)
        {
            warnings.Add(new SyncWarning(id, "An all-day event did not start at midnight in its own zone; the nearest day was used."));
        }

        return new AllDayEvent(first, after > first ? after : first.AddDays(1));
    }

    private static DateOnly DateOf(DateTimeOffset instant, TimeZoneInfo zone, out bool exact)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        exact = local.TimeOfDay == TimeSpan.Zero;
        var date = DateOnly.FromDateTime(local);
        return local.TimeOfDay >= TimeSpan.FromHours(12) ? date.AddDays(1) : date;
    }
}
