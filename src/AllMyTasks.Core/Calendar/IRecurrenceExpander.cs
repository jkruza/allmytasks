namespace AllMyTasks.Core.Calendar;

/// <summary>
/// Turns a recurring event into the occurrences that fall in a date range. One implementation is
/// shared by every provider. Wraps a library so it can be swapped.
/// </summary>
public interface IRecurrenceExpander
{
    /// <summary>
    /// Expands <paramref name="master"/> within [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>).
    /// </summary>
    /// <param name="master">The series. If it has no <c>Recurrence</c> the result is the event itself, if it overlaps.</param>
    /// <param name="modifiedOccurrences">
    /// Modified occurrences of this series. Each replaces the generated occurrence whose start equals its
    /// <c>Key.RecurrenceId</c>; a cancelled override removes that occurrence.
    /// </param>
    /// <param name="rangeStart">Start of the range (inclusive).</param>
    /// <param name="rangeEnd">End of the range (exclusive).</param>
    /// <param name="displayZone">Zone used to place all-day and floating events.</param>
    /// <returns>Occurrences ordered by start. Never includes cancelled ones.</returns>
    IReadOnlyList<EventOccurrence> Expand(
        CalendarEvent master,
        IEnumerable<CalendarEvent> modifiedOccurrences,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone);
}
