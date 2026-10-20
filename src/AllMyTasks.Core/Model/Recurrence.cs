namespace AllMyTasks.Core.Model;

/// <summary>
/// How an event or task repeats. The series is stored once and expanded on query.
/// </summary>
/// <param name="Rule">RFC 5545 <c>RRULE</c> value, such as <c>FREQ=WEEKLY;BYDAY=MO</c>. May be empty when only <c>RDATE</c>s are used.</param>
/// <param name="ExtraDates"><c>RDATE</c>: additional occurrences.</param>
/// <param name="ExcludedDates"><c>EXDATE</c>: occurrences that are skipped.</param>
public sealed record Recurrence(
    string Rule,
    IReadOnlyList<DateTimeOffset> ExtraDates,
    IReadOnlyList<DateTimeOffset> ExcludedDates)
{
    public Recurrence(string rule)
        : this(rule, [], [])
    {
    }

    public string Rule { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Rule);

    public IReadOnlyList<DateTimeOffset> ExtraDates { get; init => field = Guard.NotNull(value); } = Guard.NotNull(ExtraDates);

    public IReadOnlyList<DateTimeOffset> ExcludedDates { get; init => field = Guard.NotNull(value); } = Guard.NotNull(ExcludedDates);
}
