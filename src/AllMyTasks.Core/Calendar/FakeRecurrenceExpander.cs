namespace AllMyTasks.Core.Calendar;

/// <summary>
/// A stand-in expander for tests and samples that need no RRULE parsing. It ignores <c>Recurrence.Rule</c> and
/// produces occurrences only from the master's start plus its <c>ExtraDates</c>; the shared rules for exclusions,
/// overrides, cancellations and the range come from <see cref="RecurrenceExpanderBase"/>. The real expander is
/// <c>IcalRecurrenceExpander</c> in <c>AllMyTasks.Providers.ICal</c>.
/// </summary>
public sealed class FakeRecurrenceExpander : RecurrenceExpanderBase
{
    protected override IEnumerable<EventTime> GeneratedTimes(
        CalendarEvent master,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone)
    {
        yield return master.Time;

        foreach (var extra in master.Recurrence?.ExtraDates ?? [])
        {
            yield return master.Time switch
            {
                TimedEvent timed => new TimedEvent(extra, extra + (timed.End - timed.Start), timed.TimeZoneId, timed.IsFloating),
                AllDayEvent allDay => ShiftAllDay(allDay, DateOnly.FromDateTime(extra.UtcDateTime)),
                _ => throw new InvalidOperationException("Unknown event time type."),
            };
        }
    }

    private static AllDayEvent ShiftAllDay(AllDayEvent original, DateOnly newStart) =>
        new(newStart, newStart.AddDays(original.Days));
}
