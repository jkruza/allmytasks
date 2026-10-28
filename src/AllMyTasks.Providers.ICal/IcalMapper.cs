using System.Security.Cryptography;
using System.Text;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization.DataTypes;
using CoreEvent = AllMyTasks.Core.Calendar.CalendarEvent;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using Person = AllMyTasks.Core.Calendar.Attendee;

namespace AllMyTasks.Providers.ICal;

/// <summary>What parsing a feed produced.</summary>
/// <param name="Events">Events (masters and overrides) in the common model.</param>
/// <param name="Warnings">Per-item problems that did not stop the parse. Never contain item content (they may name an exception type).</param>
/// <param name="CalendarName">The <c>X-WR-CALNAME</c>, if any.</param>
public sealed record IcalParseResult(IReadOnlyList<CoreEvent> Events, IReadOnlyList<SyncWarning> Warnings, string? CalendarName);

/// <summary>Parses iCalendar text and maps VEVENTs to <see cref="CoreEvent"/>. Unmapped fields are dropped.</summary>
public static class IcalMapper
{
    internal const string BeginCalendar = "BEGIN:VCALENDAR";

    /// <summary>True when the text looks like an iCalendar document (so an HTML error page is not mistaken for an empty calendar).</summary>
    public static bool LooksLikeCalendar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.AsSpan().TrimStart('﻿').TrimStart();
        return start.StartsWith(BeginCalendar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses <paramref name="text"/> for the given collection.</summary>
    /// <exception cref="SourceDataException">The text is not an iCalendar document.</exception>
    public static IcalParseResult Parse(string text, string sourceId, string collectionId, string? idSeed = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!LooksLikeCalendar(text))
        {
            throw new SourceDataException("The response is not an iCalendar document.");
        }

        var warnings = new List<SyncWarning>();
        text = EnsureUids(text, warnings, idSeed);
        string? name = null;
        List<IcalEvent> components;
        try
        {
            var calendar = IcalCalendar.Load(text)
                ?? throw new SourceDataException("The calendar could not be read.");
            name = calendar.Properties.Get<string>("X-WR-CALNAME");
            components = [.. calendar.Events];
        }
        catch (Exception ex) when (ex is not SourceException and not OperationCanceledException)
        {
            // One broken component must not cost the whole feed: retry event by event.
            return ParseLeniently(text, sourceId, collectionId, warnings);
        }

        var events = new List<CoreEvent>();
        foreach (var component in components)
        {
            try
            {
                events.Add(Map(component, sourceId, collectionId, warnings));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new SyncWarning(component.Uid, $"An event could not be read and was skipped ({ex.GetType().Name})."));
            }
        }

        return new IcalParseResult(events, warnings, name);
    }

    private static IcalParseResult ParseLeniently(string text, string sourceId, string collectionId, List<SyncWarning> warnings)
    {
        var events = new List<CoreEvent>();
        var normalized = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var timeZones = new StringBuilder();
        var blocks = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inTimeZone = false;
        string? name = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("X-WR-CALNAME:", StringComparison.OrdinalIgnoreCase))
            {
                name = line[13..];
            }

            if (line.StartsWith("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                inTimeZone = true;
            }

            if (inTimeZone)
            {
                timeZones.Append(line).Append("\r\n");
                if (line.StartsWith("END:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                {
                    inTimeZone = false;
                }

                continue;
            }

            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }

            if (depth > 0)
            {
                current.Append(line).Append("\r\n");
            }

            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase) && depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    blocks.Add(current.ToString());
                    current.Clear();
                }
            }
        }

        foreach (var block in blocks)
        {
            try
            {
                var wrapped = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\n{timeZones}{block}END:VCALENDAR\r\n";
                var calendar = IcalCalendar.Load(wrapped);
                foreach (var component in calendar!.Events)
                {
                    events.Add(Map(component, sourceId, collectionId, warnings));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(new SyncWarning(null, $"An event could not be read and was skipped ({ex.GetType().Name})."));
            }
        }

        return new IcalParseResult(events, warnings, name);
    }

    /// <summary>
    /// Ical.Net invents a random UID for events that have none, which would make every sync look like a change. Events
    /// without a UID get a stable one derived from their own text instead (prefixed so it is not mistaken for server data).
    /// </summary>
    internal static string EnsureUids(string text, List<SyncWarning> warnings, string? idSeed = null)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(text.Length + 64);
        var block = new List<string>();
        var inEvent = false;
        var index = 0;

        void Flush()
        {
            var hasUid = block.Skip(1).Any(l => l.StartsWith("UID:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("UID;", StringComparison.OrdinalIgnoreCase));
            output.Append(block[0]).Append("\r\n");
            if (!hasUid)
            {
                var id = "noid-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed is null ? string.Join('\n', block) : idSeed + "#" + index))).AsSpan(0, 16).ToString().ToLowerInvariant();
                output.Append("UID:").Append(id).Append("\r\n");
                warnings.Add(new SyncWarning(id, "An event had no UID; an id was derived from its content."));
            }

            foreach (var line in block.Skip(1))
            {
                output.Append(line).Append("\r\n");
            }

            block.Clear();
            index++;
        }

        foreach (var line in lines)
        {
            if (!inEvent && line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                block.Add(line);
                continue;
            }

            if (inEvent)
            {
                block.Add(line);
                if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inEvent = false;
                    Flush();
                }

                continue;
            }

            output.Append(line).Append("\r\n");
        }

        if (inEvent)
        {
            // An unterminated event: pass it through untouched and let the parser deal with it.
            foreach (var line in block)
            {
                output.Append(line).Append("\r\n");
            }
        }

        return output.ToString();
    }

    private static CoreEvent Map(IcalEvent ev, string sourceId, string collectionId, List<SyncWarning> warnings)
    {
        var uid = (ev.Uid ?? throw new ArgumentException("Event has no id.")).Trim();

        var start = ev.DtStart ?? throw new ArgumentException("Event has no start.");
        var (time, zone) = MapTime(ev, start, uid, warnings);

        Recurrence? recurrence = null;
        DateTimeOffset? recurrenceId = null;
        if (ev.RecurrenceIdentifier is { } rid)
        {
            recurrenceId = ToIdentifier(rid.StartTime, zone);
        }
        else
        {
            recurrence = MapRecurrence(ev, time, zone);
        }

        var key = new ItemKey(sourceId, collectionId, uid, recurrenceId);
        var attendees = Person.Cap(ev.Attendees.Select(a => PersonOf(a.CommonName, a.Value, a.ParticipationStatus, a.Role, a.Type)), out var total);
        return new CoreEvent(
            key,
            ev.Summary ?? string.Empty,
            time,
            Description: Blank(ev.Description),
            Location: Blank(ev.Location),
            Url: ev.Url,
            Status: ev.Status?.ToUpperInvariant() switch
            {
                "CANCELLED" => EventStatus.Cancelled,
                "TENTATIVE" => EventStatus.Tentative,
                _ => EventStatus.Confirmed,
            },
            Availability: string.Equals(ev.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? Availability.Free : Availability.Busy,
            Recurrence: recurrence,
            Categories: [.. ev.Categories.Where(c => !string.IsNullOrWhiteSpace(c))],
            LastModified: ev.LastModified is { } lm ? new DateTimeOffset(lm.AsUtc, TimeSpan.Zero) : null,
            MeetingUrl: MeetingUrlOf(ev),
            Organizer: ev.Organizer is { } organizer ? PersonOf(organizer.CommonName, organizer.Value, null, null, null) : null,
            Attendees: attendees,
            AttendeeCount: total);
    }

    /// <summary>
    /// A structured conferencing property first (RFC 7986 <c>CONFERENCE</c> with <c>FEATURE=VIDEO</c>, then the vendor ones), then the
    /// first link on a known meeting host in <c>LOCATION</c> and <c>DESCRIPTION</c>. An arbitrary link in free text is not a meeting link.
    /// </summary>
    internal static Uri? MeetingUrlOf(IcalEvent ev)
    {
        foreach (var property in ev.Properties.Where(p => string.Equals(p.Name, "CONFERENCE", StringComparison.OrdinalIgnoreCase)))
        {
            // FEATURE can list several values (AUDIO,VIDEO).
            var features = property.Parameters.Where(p => string.Equals(p.Name, "FEATURE", StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Values);
            if (features.Contains("VIDEO", StringComparer.OrdinalIgnoreCase)
                && MeetingLinks.Sanitize(property.Value?.ToString()) is { } video)
            {
                return video;
            }
        }

        foreach (var name in new[] { "X-GOOGLE-CONFERENCE", "X-MICROSOFT-SKYPETEAMSMEETINGURL", "X-MICROSOFT-ONLINEMEETINGEXTERNALLINK" })
        {
            var value = ev.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
            if (MeetingLinks.Sanitize(value) is { } vendor)
            {
                return vendor;
            }
        }

        return MeetingLinks.FindInText(ev.Location) ?? MeetingLinks.FindInText(ev.Description);
    }

    /// <summary>A person from the <c>mailto:</c> value and parameters of an ORGANIZER or ATTENDEE line.</summary>
    internal static Person PersonOf(string? commonName, Uri? value, string? partStat, string? role, string? calendarUserType)
    {
        // Ical.Net puts "mailto:" in front of any value, so a value that is not an address (no "@") is not an e-mail.
        var address = value is not null && value.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase) && Uri.UnescapeDataString(value.AbsoluteUri[7..]) is var mail && mail.Contains('@', StringComparison.Ordinal) ? mail : null;
        var kind = calendarUserType?.ToUpperInvariant();
        var attendeeRole = kind is "RESOURCE" or "ROOM" || string.Equals(role, "NON-PARTICIPANT", StringComparison.OrdinalIgnoreCase)
            ? AttendeeRole.Resource
            : string.Equals(role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase) ? AttendeeRole.Optional : AttendeeRole.Required;
        var response = partStat?.ToUpperInvariant() switch
        {
            "ACCEPTED" => ResponseStatus.Accepted,
            "TENTATIVE" => ResponseStatus.Tentative,
            "DECLINED" => ResponseStatus.Declined,
            _ => ResponseStatus.NeedsAction,
        };
        return new Person(commonName, address, attendeeRole, response);
    }

    private static (EventTime Time, TimeZoneInfo? Zone) MapTime(IcalEvent ev, CalDateTime start, string uid, List<SyncWarning> warnings)
    {
        var end = ev.DtEnd;

        if (!start.HasTime)
        {
            var first = start.Date;
            DateOnly last;
            if (end is not null)
            {
                last = end.Date;
            }
            else if (ev.Duration is { } duration)
            {
                last = first.AddDays(Math.Max(1, DurationDays(duration)));
            }
            else
            {
                last = first.AddDays(1);
            }

            if (last <= first)
            {
                last = first.AddDays(1);
            }

            return (new AllDayEvent(first, last), null);
        }

        var (startInstant, zoneId, isFloating, zone) = Instant(start, uid, warnings);
        DateTimeOffset endInstant;
        if (end is not null)
        {
            endInstant = Instant(end, uid, warnings, floatingAsUtc: isFloating).Instant;
        }
        else if (ev.Duration is { } duration)
        {
            endInstant = startInstant + ToTimeSpan(duration);
        }
        else
        {
            endInstant = startInstant;
        }

        if (endInstant < startInstant)
        {
            warnings.Add(new SyncWarning(uid, "An event ended before it started; its end was set to its start."));
            endInstant = startInstant;
        }

        return (new TimedEvent(startInstant, endInstant, zoneId, isFloating), zone);
    }

    private static (DateTimeOffset Instant, string? ZoneId, bool IsFloating, TimeZoneInfo? Zone) Instant(
        CalDateTime value,
        string uid,
        List<SyncWarning> warnings,
        bool floatingAsUtc = false)
    {
        if (value.IsUtc)
        {
            return (new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)), "UTC", false, TimeZoneInfo.Utc);
        }

        if (string.IsNullOrEmpty(value.TzId) || floatingAsUtc)
        {
            // Floating: the wall clock is what counts; it is stored with offset zero (see TimedEvent.IsFloating).
            return (new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), TimeSpan.Zero), null, true, null);
        }

        if (ZoneResolver.Resolve(value.TzId) is { } resolved)
        {
            return (ZoneResolver.ToInstant(value.Value, resolved.Zone), resolved.IanaId, false, resolved.Zone);
        }

        warnings.Add(new SyncWarning(uid, "An event used an unknown time zone; UTC was assumed."));
        return (new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)), "UTC", false, TimeZoneInfo.Utc);
    }

    private static Recurrence? MapRecurrence(IcalEvent ev, EventTime time, TimeZoneInfo? zone)
    {
        var rule = ev.RecurrenceRule;
        var extra = ev.RecurrenceDates.GetAllDates().Select(d => ToIdentifier(d, zone)).ToList();
        var excluded = ev.ExceptionDates.GetAllDates().Select(d => ToIdentifier(d, zone)).ToList();
        if (rule is null && extra.Count == 0)
        {
            return null;
        }

        var text = rule is null ? string.Empty : new RecurrenceRuleSerializer().SerializeToString(rule) ?? string.Empty;
        text = text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? text[6..] : text;
        _ = time;
        return new Recurrence(text, extra, excluded);
    }

    /// <summary>The instant used to identify an occurrence: a date becomes midnight UTC, a floating time its wall clock as UTC.</summary>
    private static DateTimeOffset ToIdentifier(CalDateTime value, TimeZoneInfo? zone)
    {
        if (!value.HasTime)
        {
            return new DateTimeOffset(value.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (value.IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
        }

        if (!string.IsNullOrEmpty(value.TzId) && ZoneResolver.Resolve(value.TzId) is { } resolved)
        {
            return ZoneResolver.ToInstant(value.Value, resolved.Zone);
        }

        if (zone is not null && string.IsNullOrEmpty(value.TzId))
        {
            return ZoneResolver.ToInstant(value.Value, zone);
        }

        return new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    private static int DurationDays(Duration d) => ((d.Weeks ?? 0) * 7) + (d.Days ?? 0);

    private static TimeSpan ToTimeSpan(Duration d)
    {
        var span = new TimeSpan(((d.Weeks ?? 0) * 7) + (d.Days ?? 0), d.Hours ?? 0, d.Minutes ?? 0, d.Seconds ?? 0);
        return d.Sign < 0 ? -span : span;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
