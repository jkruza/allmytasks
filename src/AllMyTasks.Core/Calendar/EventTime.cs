using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Calendar;

/// <summary>
/// When an event happens. Timed and all-day events are different types on purpose: an all-day
/// event is a range of dates, not a pair of instants.
/// </summary>
public abstract record EventTime
{
    private protected EventTime()
    {
    }

    /// <summary>The first instant of the event as seen in <paramref name="zone"/>.</summary>
    public abstract DateTimeOffset StartInstant(TimeZoneInfo zone);

    /// <summary>The instant the event ends (exclusive) as seen in <paramref name="zone"/>.</summary>
    public abstract DateTimeOffset EndInstant(TimeZoneInfo zone);

    /// <summary>True when the event intersects the half-open range [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var start = StartInstant(zone);
        var end = EndInstant(zone);

        // A zero-length event is a point: it overlaps when the point lies inside the range.
        return start == end
            ? start >= from && start < to
            : start < to && end > from;
    }
}

/// <summary>An event with an exact start and end.</summary>
/// <param name="Start">The start instant.</param>
/// <param name="End">The end instant; not before <paramref name="Start"/> (zero length is allowed).</param>
/// <param name="TimeZoneId">IANA zone id the event was authored in, if known.</param>
/// <param name="IsFloating">
/// True for iCalendar times with no zone, which mean "this wall-clock time wherever you are".
/// The wall-clock reading of <paramref name="Start"/> and <paramref name="End"/> is what counts;
/// their offset is ignored and the display zone is applied instead.
/// </param>
public sealed record TimedEvent(
    DateTimeOffset Start,
    DateTimeOffset End,
    string? TimeZoneId = null,
    bool IsFloating = false) : EventTime
{
    public DateTimeOffset End { get; init; } = End >= Start
        ? End
        : throw new ArgumentOutOfRangeException(nameof(End), "End must not be before Start.");

    public override DateTimeOffset StartInstant(TimeZoneInfo zone) => Resolve(Start, zone);

    public override DateTimeOffset EndInstant(TimeZoneInfo zone) => Resolve(End, zone);

    private DateTimeOffset Resolve(DateTimeOffset value, TimeZoneInfo zone)
    {
        if (!IsFloating)
        {
            return value;
        }

        var wallClock = DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wallClock))
        {
            wallClock = wallClock.AddMinutes(30);
        }

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}

/// <summary>An event covering whole calendar days, in whatever zone the viewer is in.</summary>
/// <param name="Start">The first day.</param>
/// <param name="EndExclusive">The day after the last day, as in iCalendar <c>DTEND</c>.</param>
public sealed record AllDayEvent(DateOnly Start, DateOnly EndExclusive) : EventTime
{
    public DateOnly EndExclusive { get; init; } = EndExclusive > Start
        ? EndExclusive
        : throw new ArgumentOutOfRangeException(nameof(EndExclusive), "EndExclusive must be after Start.");

    /// <summary>Number of days covered.</summary>
    public int Days => EndExclusive.DayNumber - Start.DayNumber;

    public override DateTimeOffset StartInstant(TimeZoneInfo zone) => Start.StartOfDay(zone);

    public override DateTimeOffset EndInstant(TimeZoneInfo zone) => EndExclusive.StartOfDay(zone);
}
