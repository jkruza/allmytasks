using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class TaskDetailsTextTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");

    private static TaskItem T(string title = "x", TaskItemStatus status = TaskItemStatus.Open, DateTimeOffset? completed = null) =>
        new(new ItemKey("s", "c", "1"), title, Status: status, CompletedAt: completed);

    [Theory]
    [InlineData("", "(no title)")]
    [InlineData("   ", "(no title)")]
    [InlineData("Buy milk", "Buy milk")]
    public void Title_replaces_empty(string title, string expected) => Assert.Equal(expected, TaskDetailsText.Title(T(title)));

    [Theory]
    [InlineData(TaskItemStatus.Open, "☐")]
    [InlineData(TaskItemStatus.InProgress, "☐")]
    [InlineData(TaskItemStatus.Completed, "☑")]
    [InlineData(TaskItemStatus.Cancelled, "☒")]
    public void Glyph(TaskItemStatus status, string expected) => Assert.Equal(expected, TaskDetailsText.Glyph(T(status: status)));

    [Fact]
    public void Status_words()
    {
        Assert.Equal("Open", TaskDetailsText.Status(T(), Utc, Culture));
        Assert.Equal("In progress", TaskDetailsText.Status(T(status: TaskItemStatus.InProgress), Utc, Culture));
        Assert.Equal("Cancelled", TaskDetailsText.Status(T(status: TaskItemStatus.Cancelled), Utc, Culture));
        Assert.Equal("Completed", TaskDetailsText.Status(T(status: TaskItemStatus.Completed), Utc, Culture));
    }

    [Fact]
    public void Completed_status_includes_the_completion_time_in_the_display_zone()
    {
        var done = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

        var text = TaskDetailsText.Status(T(status: TaskItemStatus.Completed, completed: done), Plus2, Culture);

        Assert.StartsWith("Completed Tue 20 Oct 2026, ", text);
    }

    [Fact]
    public void Due_date_has_no_time_and_due_time_is_converted()
    {
        Assert.Equal("Mon 19 Oct 2026", TaskDetailsText.Due(new DueDate(new DateOnly(2026, 10, 19)), Plus2, Culture));
        var at = new DueDateTime(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero));
        Assert.StartsWith("Tue 20 Oct 2026, ", TaskDetailsText.Due(at, Plus2, Culture));
        Assert.Equal(string.Empty, TaskDetailsText.Due(null, Utc, Culture));
    }

    [Fact]
    public void Missing_start_and_modified_are_empty()
    {
        Assert.Equal(string.Empty, TaskDetailsText.Start(null, Utc, Culture));
        Assert.Equal(string.Empty, TaskDetailsText.LastModified(null, Utc, Culture));
        Assert.NotEqual(string.Empty, TaskDetailsText.Start(DateTimeOffset.UnixEpoch, Utc, Culture));
    }

    [Theory]
    [InlineData(Priority.None, "")]
    [InlineData(Priority.Low, "Low")]
    [InlineData(Priority.Medium, "Medium")]
    [InlineData(Priority.High, "High")]
    public void Priority_name(Priority priority, string expected) => Assert.Equal(expected, TaskDetailsText.Priority(priority));
}
