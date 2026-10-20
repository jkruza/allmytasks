namespace AllMyTasks.Core.Calendar;

/// <summary>
/// One concrete showing of an event, produced at query time. For a single event there is one;
/// for a recurring series there is one per repeat in the requested range. Never stored.
/// </summary>
/// <param name="Event">The event this is an occurrence of (the override, if <paramref name="IsOverride"/>).</param>
/// <param name="Time">When this occurrence happens, which may differ from <c>Event.Time</c> for series members.</param>
/// <param name="IsOverride">True when a modified occurrence replaced the generated one.</param>
public sealed record EventOccurrence(CalendarEvent Event, EventTime Time, bool IsOverride = false);
