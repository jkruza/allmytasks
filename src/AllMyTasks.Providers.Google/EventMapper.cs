using System.Globalization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Google;

/// <summary>Maps Google calendars and events to the app's model. Pure: no I/O.</summary>
internal sealed class EventMapper(string sourceId)
{
    private static readonly string[] DateTimeFormats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];

    /// <summary>Null for a deleted calendar. A calendar the user does not show in Google starts hidden here too.</summary>
    public CollectionInfo? MapCalendar(CalendarListEntryDto calendar)
    {
        if (calendar.Id is not { Length: > 0 } id || calendar.Deleted)
        {
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(calendar.SummaryOverride) ? calendar.SummaryOverride : calendar.Summary;
        return new CollectionInfo(sourceId, id, name ?? string.Empty, CollectionKind.Calendar, ColorFor(calendar.BackgroundColor), IsVisible: calendar.Selected && !calendar.Hidden, IsReadOnly: true);
    }

    internal static string? ColorFor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;

    /// <summary>Entries that are not events to show: Google's one-per-day working-location markers.</summary>
    public static bool IsIgnored(EventDto dto) => string.Equals(dto.EventType, "workingLocation", StringComparison.Ordinal);

    /// <summary>A deleted event or series. A cancelled single occurrence of a series is not a removal: it is kept as an override.</summary>
    public static bool IsRemoval(EventDto dto) =>
        string.Equals(dto.Status, "cancelled", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(dto.RecurringEventId);

    /// <summary>Maps one event. Returns null (with a warning) when it cannot be read; softer problems add a warning and keep the event.</summary>
    public CalendarEvent? MapEvent(string collectionId, EventDto dto, List<SyncWarning> warnings)
    {
        if (string.IsNullOrEmpty(dto.Id))
        {
            return null;
        }

        var cancelled = string.Equals(dto.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
        var key = new ItemKey(sourceId, collectionId, dto.Id);
        var original = TimeOf(dto.OriginalStartTime, dto.OriginalStartTime, out var originalAllDay);
        if (!string.IsNullOrEmpty(dto.RecurringEventId))
        {
            if (original is null)
            {
                warnings.Add(new SyncWarning(dto.Id, "A changed occurrence of a repeating event has no original start and is shown on its own."));
            }
            else
            {
                key = new ItemKey(sourceId, collectionId, dto.RecurringEventId, original.Value.Start);
            }
        }

        // A cancelled occurrence comes with little more than its original start; that is all the override needs.
        var time = TimeOf(dto.Start, dto.End, out var allDay) is { } own ? Build(own, allDay, dto.Start, dto.Id, warnings) : null;
        if (time is null && cancelled && original is { } fallback)
        {
            time = Build(fallback, originalAllDay, dto.OriginalStartTime, dto.Id, warnings);
        }

        if (time is null)
        {
            warnings.Add(new SyncWarning(dto.Id, "An event has a start or end that could not be read and was skipped."));
            return null;
        }

        Recurrence? recurrence = null;
        if (dto.Recurrence is { Count: > 0 })
        {
            recurrence = ParseRecurrence(dto.Id, dto.Recurrence, ZoneOf(dto.Start), warnings);
        }

        var attendees = Attendee.Cap(
            (dto.Attendees ?? []).Select(a => new Attendee(
                a.DisplayName,
                a.Email,
                a.Resource ? AttendeeRole.Resource : a.Optional ? AttendeeRole.Optional : AttendeeRole.Required,
                ResponseOf(a.ResponseStatus),
                a.Self)),
            out var total);

        return new CalendarEvent(
            key,
            dto.Summary ?? string.Empty,
            time,
            DescriptionOf(dto.Description),
            string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location,
            Text.HttpsUri(dto.HtmlLink),
            MapStatus(dto, cancelled),
            string.Equals(dto.Transparency, "transparent", StringComparison.OrdinalIgnoreCase) ? Availability.Free : Availability.Busy,
            recurrence,
            Categories: null,
            LastModified: Text.ParseInstant(dto.Updated),
            MeetingUrl: MeetingUrlOf(dto),
            Organizer: dto.Organizer is { } o ? new Attendee(o.DisplayName, o.Email, AttendeeRole.Required, ResponseStatus.Accepted, o.Self) : null,
            Attendees: attendees,
            AttendeeCount: total);
    }

    /// <summary>The video entry point of the conference data, else the classic <c>hangoutLink</c>. Other entry points (phone, SIP) are not links to open.</summary>
    internal static Uri? MeetingUrlOf(EventDto dto) =>
        dto.ConferenceData?.EntryPoints?.Where(e => string.Equals(e.EntryPointType, "video", StringComparison.OrdinalIgnoreCase)).Select(e => MeetingLinks.Sanitize(e.Uri)).FirstOrDefault(u => u is not null)
        ?? MeetingLinks.Sanitize(dto.HangoutLink);

    private static ResponseStatus ResponseOf(string? status) => status?.ToLowerInvariant() switch
    {
        "accepted" => ResponseStatus.Accepted,
        "tentative" => ResponseStatus.Tentative,
        "declined" => ResponseStatus.Declined,
        _ => ResponseStatus.NeedsAction,
    };

    internal static EventStatus MapStatus(EventDto dto, bool cancelled)
    {
        if (cancelled || dto.Attendees?.Any(a => a.Self && string.Equals(a.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return EventStatus.Cancelled;
        }

        return string.Equals(dto.Status, "tentative", StringComparison.OrdinalIgnoreCase) ? EventStatus.Tentative : EventStatus.Confirmed;
    }

    private static string? DescriptionOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // Google descriptions can hold simple HTML; the app shows text only.
        return description.Contains('<', StringComparison.Ordinal) && description.Contains('>', StringComparison.Ordinal) ? Text.HtmlToPlain(description) : description;
    }

    private static TimeZoneInfo? ZoneOf(EventTimeDto? time) => Zones.Find(time?.TimeZone);

    /// <summary>The first and last instant of one side of an event, and whether the event is all-day. Null when unreadable.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End)? TimeOf(EventTimeDto? start, EventTimeDto? end, out bool allDay)
    {
        allDay = false;
        if (start?.Date is { } startDate)
        {
            if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            {
                return null;
            }

            var after = end?.Date is { } endDate && DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed > first
                ? parsed
                : first.AddDays(1);
            allDay = true;
            return (Midnight(first), Midnight(after));
        }

        if (Text.ParseInstant(start?.DateTime) is { } from)
        {
            return (from, Text.ParseInstant(end?.DateTime) is { } to && to >= from ? to : from);
        }

        return null;
    }

    private static DateTimeOffset Midnight(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static EventTime? Build((DateTimeOffset Start, DateTimeOffset End) range, bool allDay, EventTimeDto? startSide, string id, List<SyncWarning> warnings)
    {
        if (allDay)
        {
            return new AllDayEvent(DateOnly.FromDateTime(range.Start.UtcDateTime), DateOnly.FromDateTime(range.End.UtcDateTime));
        }

        var zone = startSide?.TimeZone;
        if (!string.IsNullOrWhiteSpace(zone) && Zones.Find(zone) is null)
        {
            warnings.Add(new SyncWarning(id, "An event has a time zone this version does not know."));
            zone = null;
        }

        return new TimedEvent(range.Start, range.End, string.IsNullOrWhiteSpace(zone) ? null : zone.Trim());
    }

    /// <summary>Turns the iCalendar lines of <c>recurrence[]</c> into a rule and its extra and excluded dates.</summary>
    internal static Recurrence? ParseRecurrence(string id, List<string> lines, TimeZoneInfo? eventZone, List<SyncWarning> warnings)
    {
        string? rule = null;
        var extra = new List<DateTimeOffset>();
        var excluded = new List<DateTimeOffset>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon].Split(';');
            var name = head[0].ToUpperInvariant();
            var value = line[(colon + 1)..];
            switch (name)
            {
                case "RRULE" when rule is null:
                    rule = value;
                    break;
                case "RRULE":
                    warnings.Add(new SyncWarning(id, "An event has more than one repeat rule; only the first is used."));
                    break;
                case "EXDATE" or "RDATE":
                    var zone = head.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0].Equals("TZID", StringComparison.OrdinalIgnoreCase)).Select(p => Zones.Find(p[1])).FirstOrDefault() ?? eventZone;
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (ParseDate(part, zone) is { } date)
                        {
                            (name == "EXDATE" ? excluded : extra).Add(date);
                        }
                        else
                        {
                            warnings.Add(new SyncWarning(id, "A date in a repeating event could not be read and was left out."));
                        }
                    }

                    break;
            }
        }

        return rule is null && extra.Count == 0 ? null : new Recurrence(rule ?? string.Empty, extra, excluded);
    }

    private static DateTimeOffset? ParseDate(string text, TimeZoneInfo? zone)
    {
        if (!DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }

        if (text.EndsWith('Z') || text.Length == 8)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return Zones.ToInstant(wall, zone ?? TimeZoneInfo.Utc);
    }
}
