using AllMyTasks.Core.Calendar;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using CoreEvent = AllMyTasks.Core.Calendar.CalendarEvent;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace AllMyTasks.Providers.ICal;

/// <summary>
/// Expands RFC 5545 recurrence rules (<c>RRULE</c>, <c>RDATE</c>) with Ical.Net. Exclusions, overrides, cancellations
/// and the range are applied by <see cref="RecurrenceExpanderBase"/>, so they behave the same as in tests with the
/// fake. Generation is bounded: a series yields at most <see cref="RecurrenceExpanderBase.MaxOccurrencesPerSeries"/>
/// occurrences per query and Ical.Net's own guard stops impossible rules.
/// </summary>
public sealed class IcalRecurrenceExpander : RecurrenceExpanderBase
{
    private static readonly EvaluationOptions Options = new() { MaxUnmatchedIncrementsLimit = 1000 };

    protected override IEnumerable<EventTime> GeneratedTimes(
        CoreEvent master,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone)
    {
        if (master.Recurrence is null || string.IsNullOrWhiteSpace(master.Recurrence.Rule) && master.Recurrence.ExtraDates.Count == 0)
        {
            yield return master.Time;
            yield break;
        }

        // The master's own time is always an occurrence (it is DTSTART), whether or not the rule also generates it.
        yield return master.Time;

        switch (master.Time)
        {
            case TimedEvent timed:
                foreach (var time in ExpandTimed(master, timed, rangeStart, rangeEnd))
                {
                    yield return time;
                }

                break;
            case AllDayEvent allDay:
                foreach (var time in ExpandAllDay(master, allDay, rangeStart, rangeEnd))
                {
                    yield return time;
                }

                break;
        }
    }

    private static IEnumerable<EventTime> ExpandTimed(CoreEvent master, TimedEvent timed, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var duration = timed.End - timed.Start;
        TimeZoneInfo zone;
        string? tzId;
        if (timed.IsFloating)
        {
            zone = TimeZoneInfo.Utc;
            tzId = null;
        }
        else if (ZoneResolver.Resolve(timed.TimeZoneId) is { } resolved)
        {
            zone = resolved.Zone;
            tzId = resolved.IanaId;
        }
        else
        {
            zone = TimeZoneInfo.Utc;
            tzId = "UTC";
        }

        CalDateTime Local(DateTimeOffset instant) =>
            tzId is null
                ? new CalDateTime(DateTime.SpecifyKind(instant.DateTime, DateTimeKind.Unspecified), true)
                : new CalDateTime(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified), tzId, true);

        var series = BuildSeries(master, Local(timed.Start));
        var from = Local(rangeStart - duration - TimeSpan.FromDays(2));
        var until = rangeEnd + TimeSpan.FromDays(2);

        foreach (var occurrence in series.GetOccurrences(from, Options))
        {
            var startTime = occurrence.Period.StartTime;
            var start = tzId is null
                ? new DateTimeOffset(DateTime.SpecifyKind(startTime.Value, DateTimeKind.Unspecified), TimeSpan.Zero)
                : ZoneResolver.ToInstant(startTime.Value, zone);
            if (start > until)
            {
                yield break;
            }

            if (start == timed.Start)
            {
                continue;
            }

            yield return new TimedEvent(start, start + duration, timed.TimeZoneId, timed.IsFloating);
        }
    }

    private static IEnumerable<EventTime> ExpandAllDay(CoreEvent master, AllDayEvent allDay, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var series = BuildSeries(master, new CalDateTime(allDay.Start));
        var from = new CalDateTime(DateOnly.FromDateTime(rangeStart.UtcDateTime.AddDays(-allDay.Days - 2)));
        var until = DateOnly.FromDateTime(rangeEnd.UtcDateTime.AddDays(2));

        foreach (var occurrence in series.GetOccurrences(from, Options))
        {
            var date = occurrence.Period.StartTime.Date;
            if (date > until)
            {
                yield break;
            }

            if (date == allDay.Start)
            {
                continue;
            }

            yield return new AllDayEvent(date, date.AddDays(allDay.Days));
        }
    }

    private static IcalCalendar BuildSeries(CoreEvent master, CalDateTime start)
    {
        var ev = new IcalEvent { Uid = master.Key.ItemId, DtStart = start };
        if (!string.IsNullOrWhiteSpace(master.Recurrence!.Rule))
        {
            ev.RecurrenceRule = new RecurrenceRule(master.Recurrence.Rule);
        }

        foreach (var extra in master.Recurrence.ExtraDates)
        {
            ev.RecurrenceDates.Add(ToCalDateTime(extra, master.Time, start));
        }

        var calendar = new IcalCalendar();
        calendar.Events.Add(ev);
        return calendar;
    }

    private static CalDateTime ToCalDateTime(DateTimeOffset identifier, EventTime time, CalDateTime start)
    {
        if (time is AllDayEvent)
        {
            return new CalDateTime(DateOnly.FromDateTime(identifier.UtcDateTime));
        }

        // RDATEs are stored as instants; express them in the series' own zone so wall-clock times stay put across DST.
        return string.IsNullOrEmpty(start.TzId)
            ? new CalDateTime(DateTime.SpecifyKind(identifier.DateTime, DateTimeKind.Unspecified), true)
            : new CalDateTime(
                DateTime.SpecifyKind(
                    ZoneResolver.Resolve(start.TzId) is { } z ? TimeZoneInfo.ConvertTime(identifier, z.Zone).DateTime : identifier.UtcDateTime,
                    DateTimeKind.Unspecified),
                start.TzId,
                true);
    }
}
