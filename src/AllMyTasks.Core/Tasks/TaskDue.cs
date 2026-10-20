using AllMyTasks.Core.Time;

namespace AllMyTasks.Core.Tasks;

/// <summary>
/// When a task is due. "Due Friday" and "due Friday 17:00" are different things, so they are
/// different types.
/// </summary>
public abstract record TaskDue
{
    private protected TaskDue()
    {
    }

    /// <summary>The instant used to order and compare due values, as seen in <paramref name="zone"/>.</summary>
    /// <remarks>A date-only due value sorts at the start of that day, so it comes before timed tasks on the same day.</remarks>
    public abstract DateTimeOffset SortKey(TimeZoneInfo zone);

    /// <summary>True when the due value has passed at <paramref name="now"/>, as seen in <paramref name="zone"/>.</summary>
    /// <remarks>A date-only task is overdue from the day after its date; a timed task from its time.</remarks>
    public abstract bool IsPast(DateTimeOffset now, TimeZoneInfo zone);
}

/// <summary>Due on a calendar day, with no time.</summary>
public sealed record DueDate(DateOnly Date) : TaskDue
{
    public override DateTimeOffset SortKey(TimeZoneInfo zone) => Date.StartOfDay(zone);

    public override bool IsPast(DateTimeOffset now, TimeZoneInfo zone) =>
        now >= Date.AddDays(1).StartOfDay(zone);
}

/// <summary>Due at an exact time.</summary>
/// <param name="At">The instant.</param>
/// <param name="TimeZoneId">IANA zone id the due time was authored in, if known.</param>
public sealed record DueDateTime(DateTimeOffset At, string? TimeZoneId = null) : TaskDue
{
    public override DateTimeOffset SortKey(TimeZoneInfo zone) => At;

    public override bool IsPast(DateTimeOffset now, TimeZoneInfo zone) => now > At;
}
