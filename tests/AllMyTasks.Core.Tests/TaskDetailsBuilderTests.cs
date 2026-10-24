using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class TaskDetailsBuilderTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
    private static readonly ItemKey Key = new("s", "c", "1");

    private static TaskDetailsContent Build(
        TaskItem task,
        TimeZoneInfo? zone = null,
        bool overdue = false,
        string collection = "Groceries",
        string source = "Todoist",
        string? color = "#112233",
        TaskItem? parent = null,
        params TaskItem[] subtasks) =>
        TaskDetailsBuilder.Create(new TaskDetails(task, collection, color, source, parent, subtasks, overdue), zone ?? Utc, Culture);

    private static string? Row(TaskDetailsContent content, string label) => content.Rows.FirstOrDefault(r => r.Label == label)?.Value;

    [Fact]
    public void Sparse_task_shows_only_status_and_list()
    {
        var content = Build(new TaskItem(Key, "Buy milk"));

        Assert.Equal("Buy milk", content.Title);
        Assert.Equal(["Status", "List"], content.Rows.Select(r => r.Label));
        Assert.Equal("Open", Row(content, "Status"));
        Assert.Equal("Groceries, from Todoist", Row(content, "List"));
        Assert.Null(content.Notes);
        Assert.Null(content.Link);
        Assert.Null(content.Parent);
        Assert.Empty(content.Subtasks);
        Assert.Equal("#112233", content.AccentColor);
    }

    [Fact]
    public void Full_task_shows_every_field()
    {
        var task = new TaskItem(
            Key,
            "Plan trip",
            "Book the flights",
            new DueDate(new DateOnly(2026, 10, 19)),
            new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
            TaskItemStatus.InProgress,
            Priority: Priority.High,
            Labels: ["travel", "urgent"],
            Recurrence: new Recurrence("FREQ=WEEKLY"),
            Url: new Uri("https://example.com/t/1"),
            LastModified: new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero));

        var content = Build(task, overdue: true);

        Assert.Equal("In progress, overdue", Row(content, "Status"));
        Assert.Equal("Mon 19 Oct 2026", Row(content, "Due"));
        Assert.StartsWith("Mon 12 Oct 2026, ", Row(content, "Starts"));
        Assert.Equal("High", Row(content, "Priority"));
        Assert.Equal("travel, urgent", Row(content, "Labels"));
        Assert.NotNull(Row(content, "Repeats"));
        Assert.StartsWith("Sun 18 Oct 2026, ", Row(content, "Last changed"));
        Assert.Equal("Book the flights", content.Notes);
        Assert.Equal(new Uri("https://example.com/t/1"), content.Link);
        Assert.True(content.IsOverdue);
    }

    [Theory]
    [InlineData("", "(no title)")]
    [InlineData("  ", "(no title)")]
    public void Empty_title_gets_a_placeholder(string title, string expected) =>
        Assert.Equal(expected, Build(new TaskItem(Key, title)).Title);

    [Theory]
    [InlineData(TaskItemStatus.Open, "☐")]
    [InlineData(TaskItemStatus.InProgress, "☐")]
    [InlineData(TaskItemStatus.Completed, "☑")]
    [InlineData(TaskItemStatus.Cancelled, "☒")]
    public void Glyph_follows_status(TaskItemStatus status, string expected) =>
        Assert.Equal(expected, Build(new TaskItem(Key, "x", Status: status)).Glyph);

    [Fact]
    public void Completed_status_has_the_completion_time_in_the_display_zone()
    {
        var done = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

        var content = Build(new TaskItem(Key, "x", Status: TaskItemStatus.Completed, CompletedAt: done), Plus2);

        Assert.StartsWith("Completed Tue 20 Oct 2026, ", Row(content, "Status"));
    }

    [Fact]
    public void Completed_without_a_time_and_cancelled()
    {
        Assert.Equal("Completed", Row(Build(new TaskItem(Key, "x", Status: TaskItemStatus.Completed)), "Status"));
        Assert.Equal("Cancelled", Row(Build(new TaskItem(Key, "x", Status: TaskItemStatus.Cancelled)), "Status"));
    }

    [Fact]
    public void Due_time_is_converted_to_the_display_zone()
    {
        var task = new TaskItem(Key, "x", Due: new DueDateTime(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero)));

        Assert.StartsWith("Tue 20 Oct 2026, ", Row(Build(task, Plus2), "Due"));
    }

    [Fact]
    public void Open_overdue_task_says_so() =>
        Assert.Equal("Open, overdue", Row(Build(new TaskItem(Key, "x"), overdue: true), "Status"));

    [Fact]
    public void List_shows_only_the_source_when_the_list_is_unknown_and_nothing_when_both_are()
    {
        Assert.Equal("Todoist", Row(Build(new TaskItem(Key, "x"), collection: string.Empty), "List"));
        Assert.Null(Row(Build(new TaskItem(Key, "x"), collection: string.Empty, source: string.Empty), "List"));
        Assert.Equal("Inbox", Row(Build(new TaskItem(Key, "x"), collection: "Inbox", source: "Inbox"), "List"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public void Links_other_than_http_and_https_are_not_offered(string url) =>
        Assert.Null(Build(new TaskItem(Key, "x", Url: new Uri(url))).Link);

    [Fact]
    public void Long_notes_are_capped()
    {
        var notes = new string('a', TaskDetailsBuilder.MaxNotesLength + 50);

        var shown = Build(new TaskItem(Key, "x", notes)).Notes;

        Assert.Equal(TaskDetailsBuilder.MaxNotesLength + 1, shown!.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void Whitespace_notes_are_dropped() => Assert.Null(Build(new TaskItem(Key, "x", "  \n ")).Notes);

    [Fact]
    public void Missing_colour_falls_back_to_the_default() =>
        Assert.Equal(ColorUtil.DefaultColor, Build(new TaskItem(Key, "x"), color: null).AccentColor);

    [Fact]
    public void Parent_and_subtasks_are_references_with_glyphs()
    {
        var parent = new TaskItem(new ItemKey("s", "c", "p"), "Parent");
        var done = new TaskItem(new ItemKey("s", "c", "d"), "Done sub", Status: TaskItemStatus.Completed);
        var untitled = new TaskItem(new ItemKey("s", "c", "u"), string.Empty);

        var content = Build(new TaskItem(Key, "x"), parent: parent, subtasks: [untitled, done]);

        Assert.Equal(new TaskRef(parent.Key, "Parent", "☐"), content.Parent);
        Assert.Equal(
            [new TaskRef(untitled.Key, "(no title)", "☐"), new TaskRef(done.Key, "Done sub", "☑")],
            content.Subtasks);
    }
}
