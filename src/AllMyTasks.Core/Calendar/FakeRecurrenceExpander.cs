namespace AllMyTasks.Core.Calendar;

/// <summary>
/// A stand-in expander for tests and samples until the real RRULE implementation arrives (M5).
/// It ignores <c>Recurrence.Rule</c> and produces occurrences only from the master's start plus
/// its <c>ExtraDates</c>, minus its <c>ExcludedDates</c>, so the override, cancellation and range
/// rules of the contract can be exercised without parsing RRULE.
/// </summary>
public sealed class FakeRecurrenceExpander : IRecurrenceExpander
{
    public IReadOnlyList<EventOccurrence> Expand(
        CalendarEvent master,
        IEnumerable<CalendarEvent> modifiedOccurrences,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(modifiedOccurrences);
        ArgumentNullException.ThrowIfNull(displayZone);

        var occurrences = new List<EventOccurrence>();
        var overridesByStart = modifiedOccurrences
            .Where(o => o.Key.RecurrenceId is not null)
            .ToDictionary(o => o.Key.RecurrenceId!.Value);

        foreach (var original in GeneratedTimes(master))
        {
            var start = original.StartInstant(displayZone);
            var recurrenceId = RecurrenceIdOf(original, start);

            if (master.Recurrence?.ExcludedDates.Contains(recurrenceId) == true)
            {
                continue;
            }

            if (overridesByStart.TryGetValue(recurrenceId, out var replacement))
            {
                if (replacement.Status != EventStatus.Cancelled && replacement.Time.Overlaps(rangeStart, rangeEnd, displayZone))
                {
                    occurrences.Add(new EventOccurrence(replacement, replacement.Time, IsOverride: true));
                }

                continue;
            }

            if (master.Status != EventStatus.Cancelled && original.Overlaps(rangeStart, rangeEnd, displayZone))
            {
                occurrences.Add(new EventOccurrence(master, original));
            }
        }

        return [.. occurrences.OrderBy(o => o.Time.StartInstant(displayZone))];
    }

    // All-day occurrences are identified by midnight UTC of their date (see ItemKey.RecurrenceId).
    private static DateTimeOffset RecurrenceIdOf(EventTime time, DateTimeOffset start) =>
        time is AllDayEvent allDay
            ? new DateTimeOffset(allDay.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : start;

    private static IEnumerable<EventTime> GeneratedTimes(CalendarEvent master)
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
