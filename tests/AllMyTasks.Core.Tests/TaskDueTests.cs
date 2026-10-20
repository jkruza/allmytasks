using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

public class TaskDueTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Date_only_due_is_not_past_during_its_day()
    {
        var due = new DueDate(new DateOnly(2026, 10, 19));

        Assert.False(due.IsPast(new DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero), Utc));
        Assert.True(due.IsPast(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), Utc));
    }

    [Fact]
    public void Timed_due_is_past_right_after_its_time()
    {
        var at = new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero);
        var due = new DueDateTime(at);

        Assert.False(due.IsPast(at, Utc));
        Assert.True(due.IsPast(at.AddSeconds(1), Utc));
    }

    [Fact]
    public void Date_only_day_is_judged_in_the_display_zone()
    {
        // 23:30 UTC on the 19th is already 01:30 on the 20th in Warsaw (UTC+2).
        var due = new DueDate(new DateOnly(2026, 10, 19));
        var now = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

        Assert.False(due.IsPast(now, Utc));
        Assert.True(due.IsPast(now, TestData.Warsaw));
    }

    [Fact]
    public void Date_only_sorts_before_timed_on_the_same_day()
    {
        var date = new DueDate(new DateOnly(2026, 10, 19));
        var timed = new DueDateTime(new DateTimeOffset(2026, 10, 19, 0, 0, 1, TimeSpan.Zero));

        Assert.True(date.SortKey(Utc) < timed.SortKey(Utc));
    }

    [Fact]
    public void Date_and_timed_values_are_never_equal() =>
        Assert.NotEqual<TaskDue>(
            new DueDate(new DateOnly(2026, 10, 19)),
            new DueDateTime(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero)));
}
