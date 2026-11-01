using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

/// <summary>The Inbox is the All tasks list with <see cref="TaskFilter.Inbox"/>; see docs/design/inbox-view.md.</summary>
public class InboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TaskItem T(string id, string title, TaskDue? due = null, TaskItemStatus status = TaskItemStatus.Open, string collection = "c",
        string? parent = null, DateTimeOffset? start = null, DateTimeOffset? modified = null) =>
        new(new ItemKey("s", collection, id), title, null, due, Status: status, ParentKey: parent is null ? null : new ItemKey("s", collection, parent), Start: start, LastModified: modified);

    private static async Task<(TaskListService Service, string[] Titles)> RunAsync(params TaskItem[] tasks)
    {
        var store = new InMemoryStore();
        var ct = TestContext.Current.CancellationToken;
        await store.SaveCollectionsAsync(
            new SourceInfo("s", "t", "Source S"),
            [
                new CollectionInfo("s", "c", "Work", CollectionKind.TaskList),
                new CollectionInfo("s", "d", "Home", CollectionKind.TaskList),
                new CollectionInfo("s", "hidden", "Hidden", CollectionKind.TaskList, IsVisible: false),
            ],
            ct);
        foreach (var group in tasks.GroupBy(t => t.Key.CollectionId))
        {
            await store.ApplyAsync("s", group.Key, SyncResult.Snapshot<TaskItem>([.. group], new SyncState("x")), ct);
        }

        var service = new TaskListService(store, store, new FixedClock(Now));
        var groups = await service.GetAsync(new TaskListQuery(TaskFilter.Inbox, new TaskSort(TaskSortField.LastModified, Descending: true), TaskGrouping.Collection, Utc), ct);
        return (service, [.. groups.SelectMany(g => g.Rows).SelectMany(Flatten).Select(r => r.Task.Title)]);
    }

    private static IEnumerable<TaskRow> Flatten(TaskRow row) => new[] { row }.Concat(row.Children.SelectMany(Flatten));

    [Fact]
    public async Task Holds_open_tasks_without_a_due_date_only()
    {
        var (_, titles) = await RunAsync(
            T("1", "undated"),
            T("2", "dated", new DueDate(new DateOnly(2026, 11, 3))),
            T("3", "done", status: TaskItemStatus.Completed),
            T("4", "dropped", status: TaskItemStatus.Cancelled),
            T("5", "in progress", status: TaskItemStatus.InProgress));

        Assert.Equal(["in progress", "undated"], titles.Order());
    }

    [Fact]
    public async Task A_start_date_alone_does_not_make_a_task_dated()
    {
        var (_, titles) = await RunAsync(T("1", "starts later", start: Now.AddDays(3)));

        Assert.Equal(["starts later"], titles);
    }

    [Fact]
    public async Task Includes_every_visible_collection_and_skips_hidden_ones()
    {
        var (_, titles) = await RunAsync(T("1", "work", collection: "c"), T("2", "home", collection: "d"), T("3", "secret", collection: "hidden"));

        Assert.Equal(["home", "work"], titles.Order());
    }

    [Fact]
    public async Task Newest_modification_comes_first_and_groups_follow_collections()
    {
        var (_, titles) = await RunAsync(
            T("1", "old", modified: Now.AddDays(-5)),
            T("2", "new", modified: Now.AddDays(-1)),
            T("3", "home", collection: "d", modified: Now));

        // Home sorts before Work; inside Work, the newer task is first.
        Assert.Equal(["home", "new", "old"], titles);
    }

    [Fact]
    public async Task Subtasks_nest_under_their_parent_or_stand_alone_when_the_parent_is_filtered_out()
    {
        var (_, nested) = await RunAsync(T("p", "parent"), T("c", "child", parent: "p"));
        var (_, alone) = await RunAsync(T("p", "dated parent", new DueDate(new DateOnly(2026, 11, 3))), T("c", "child", parent: "p"));

        Assert.Equal(["parent", "child"], nested);
        Assert.Equal(["child"], alone);
    }

    [Fact]
    public async Task Count_matches_the_list_and_ignores_hidden_collections()
    {
        var (service, titles) = await RunAsync(
            T("1", "a"),
            T("2", "b", collection: "d"),
            T("3", "child", parent: "1"),
            T("4", "dated", new DueDate(new DateOnly(2026, 11, 3))),
            T("5", "secret", collection: "hidden"));

        var count = await service.CountAsync(TaskFilter.Inbox, Utc, TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal(titles.Length, count);
    }

    [Fact]
    public async Task Count_is_zero_when_nothing_is_undated()
    {
        var (service, _) = await RunAsync(T("1", "dated", new DueDate(new DateOnly(2026, 11, 3))));

        Assert.Equal(0, await service.CountAsync(TaskFilter.Inbox, Utc, TestContext.Current.CancellationToken));
    }
}
