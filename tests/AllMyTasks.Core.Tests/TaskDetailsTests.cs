using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class TaskDetailsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ItemKey Root = new("s", "c", "root");

    private static TaskItem T(string id, string title, string? parent = null, TaskDue? due = null, TaskItemStatus status = TaskItemStatus.Open) =>
        new(new ItemKey("s", "c", id), title, Due: due, Status: status, ParentKey: parent is null ? null : new ItemKey("s", "c", parent));

    private static async Task<TaskDetails?> Get(ItemKey key, params TaskItem[] tasks)
    {
        var store = new InMemoryStore();
        await store.SaveCollectionsAsync(
            new SourceInfo("s", "t", "Source S"),
            [new CollectionInfo("s", "c", "Groceries", CollectionKind.TaskList, "#112233")],
            TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "c", SyncResult.Snapshot<TaskItem>([.. tasks], new SyncState("x")), TestContext.Current.CancellationToken);
        var service = new TaskDetailsService(store, store, new FixedClock(Now));
        return await service.GetAsync(key, TimeZoneInfo.Utc, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_task_gives_null()
    {
        Assert.Null(await Get(new ItemKey("s", "c", "nope"), T("root", "Root")));
    }

    [Fact]
    public async Task Names_the_collection_and_source()
    {
        var details = await Get(Root, T("root", "Root"));

        Assert.NotNull(details);
        Assert.Equal("Root", details.Task.Title);
        Assert.Equal("Groceries", details.CollectionName);
        Assert.Equal("#112233", details.CollectionColor);
        Assert.Equal("Source S", details.SourceName);
        Assert.Null(details.Parent);
        Assert.Empty(details.Subtasks);
    }

    [Fact]
    public async Task Finds_the_parent()
    {
        var details = await Get(new ItemKey("s", "c", "child"), T("root", "Root"), T("child", "Child", parent: "root"));

        Assert.Equal("Root", details!.Parent!.Title);
    }

    [Fact]
    public async Task Parent_that_is_not_cached_is_ignored()
    {
        var details = await Get(new ItemKey("s", "c", "child"), T("child", "Child", parent: "gone"));

        Assert.Null(details!.Parent);
    }

    [Fact]
    public async Task Subtasks_list_open_first_then_by_due_then_title()
    {
        var details = await Get(
            Root,
            T("root", "Root"),
            T("a", "done", "root", status: TaskItemStatus.Completed),
            T("b", "undated b", "root"),
            T("c", "later", "root", new DueDate(new DateOnly(2026, 11, 1))),
            T("d", "sooner", "root", new DueDate(new DateOnly(2026, 10, 25))),
            T("e", "undated a", "root"),
            T("f", "grandchild", "d"));

        Assert.Equal(["sooner", "later", "undated a", "undated b", "done"], details!.Subtasks.Select(t => t.Title));
    }

    [Fact]
    public async Task Task_that_is_its_own_parent_has_neither_parent_nor_subtask()
    {
        var details = await Get(Root, T("root", "Root", parent: "root"));

        Assert.Null(details!.Parent);
        Assert.Empty(details.Subtasks);
    }

    [Fact]
    public async Task Parent_cycle_does_not_list_the_parent_as_a_subtask()
    {
        var details = await Get(Root, T("root", "Root", parent: "child"), T("child", "Child", parent: "root"));

        Assert.Equal("Child", details!.Parent!.Title);
        Assert.Empty(details.Subtasks);
    }

    [Theory]
    [InlineData(TaskItemStatus.Open, true)]
    [InlineData(TaskItemStatus.InProgress, true)]
    [InlineData(TaskItemStatus.Completed, false)]
    [InlineData(TaskItemStatus.Cancelled, false)]
    public async Task Overdue_only_for_open_tasks_past_due(TaskItemStatus status, bool expected)
    {
        var details = await Get(Root, T("root", "Root", due: new DueDate(new DateOnly(2026, 10, 18)), status: status));

        Assert.Equal(expected, details!.IsOverdue);
    }

    [Fact]
    public async Task Open_task_due_today_is_not_overdue()
    {
        var details = await Get(Root, T("root", "Root", due: new DueDate(new DateOnly(2026, 10, 19))));

        Assert.False(details!.IsOverdue);
    }
}
