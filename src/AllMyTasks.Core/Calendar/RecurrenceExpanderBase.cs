namespace AllMyTasks.Core.Calendar;

/// <summary>
/// The part of expansion every <see cref="IRecurrenceExpander"/> shares: placing generated occurrences, dropping
/// excluded dates, substituting overrides, honouring cancellations and the range. Subclasses only say which times a
/// series generates.
/// </summary>
public abstract class RecurrenceExpanderBase : IRecurrenceExpander
{
    /// <summary>Upper bound on occurrences generated per series per query, so a runaway rule cannot hang the app.</summary>
    public const int MaxOccurrencesPerSeries = 2000;

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
            .GroupBy(o => o.Key.RecurrenceId!.Value)
            .ToDictionary(g => g.Key, g => g.Last());

        var seen = new HashSet<DateTimeOffset>();
        foreach (var original in GeneratedTimes(master, rangeStart, rangeEnd, displayZone).Take(MaxOccurrencesPerSeries))
        {
            var start = original.StartInstant(displayZone);
            var recurrenceId = RecurrenceIdOf(original, start);

            // An RDATE that repeats a date the rule already produced is one occurrence, not two.
            if (!seen.Add(recurrenceId))
            {
                continue;
            }

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

        // An override moved into the range from an original date outside it still has to show up.
        foreach (var moved in overridesByStart.Values)
        {
            var already = occurrences.Any(o => ReferenceEquals(o.Event, moved));
            if (!already && moved.Status != EventStatus.Cancelled && moved.Time.Overlaps(rangeStart, rangeEnd, displayZone))
            {
                occurrences.Add(new EventOccurrence(moved, moved.Time, IsOverride: true));
            }
        }

        return [.. occurrences.OrderBy(o => o.Time.StartInstant(displayZone))];
    }

    /// <summary>
    /// The times the series generates, in order, starting with the master's own. May stop early once past
    /// <paramref name="rangeEnd"/>; the caller applies the exact range filter.
    /// </summary>
    protected abstract IEnumerable<EventTime> GeneratedTimes(
        CalendarEvent master,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone);

    /// <summary>All-day occurrences are identified by midnight UTC of their date (see <c>ItemKey.RecurrenceId</c>).</summary>
    protected static DateTimeOffset RecurrenceIdOf(EventTime time, DateTimeOffset start) =>
        time is AllDayEvent allDay
            ? new DateTimeOffset(allDay.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : start;
}
