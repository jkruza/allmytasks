using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

public class TaskListTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TaskItem T(string id, string title, TaskDue? due = null, TaskItemStatus status = TaskItemStatus.Open, Priority priority = Priority.None,
        string[]? labels = null, string? notes = null, string? parent = null, string collection = "c", DateTimeOffset? modified = null) =>
        new(new ItemKey("s", collection, id), title, notes, due, Status: status, Priority: priority, Labels: labels, ParentKey: parent is null ? null : new ItemKey("s", collection, parent), LastModified: modified);

    private static TaskFilter With(Func<TaskFilter, TaskFilter> change) => change(TaskFilter.Default);

    private static async Task<IReadOnlyList<TaskGroup>> Run(IEnumerable<TaskItem> tasks, TaskFilter? filter = null, TaskSort? sort = null, TaskGrouping grouping = TaskGrouping.None, IEnumerable<CollectionInfo>? collections = null)
    {
        var store = new InMemoryStore();
        await store.SaveCollectionsAsync(new SourceInfo("s", "t", "Source S"), [.. collections ?? [new CollectionInfo("s", "c", "Work", CollectionKind.TaskList, "#111111")]], TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "c", SyncResult.Snapshot<TaskItem>([.. tasks], new SyncState("x")), TestContext.Current.CancellationToken);
        var service = new TaskListService(store, store, new FixedClock(Now));
        return await service.GetAsync(new TaskListQuery(filter ?? TaskFilter.Default, sort ?? new TaskSort(), grouping, Utc), TestContext.Current.CancellationToken);
    }

    private static string[] Titles(IReadOnlyList<TaskGroup> groups) => [.. groups.SelectMany(g => g.Rows).Select(r => r.Task.Title)];

    [Fact]
    public async Task Default_shows_open_tasks_only_sorted_by_due_with_undated_last()
    {
        var groups = await Run([
            T("1", "none"),
            T("2", "tomorrow", new DueDate(Today.AddDays(1))),
            T("3", "done", new DueDate(Today), TaskItemStatus.Completed),
            T("4", "today", new DueDate(Today)),
            T("5", "cancelled", status: TaskItemStatus.Cancelled),
            T("6", "doing", new DueDate(Today.AddDays(2)), TaskItemStatus.InProgress)]);

        Assert.Equal(["today", "tomorrow", "doing", "none"], Titles(groups));
    }

    [Theory]
    [InlineData(TaskStatusFilter.Completed, new[] { "done" })]
    [InlineData(TaskStatusFilter.Cancelled, new[] { "dropped" })]
    [InlineData(TaskStatusFilter.All, new[] { "done", "dropped", "open" })]
    public async Task Status_filter(TaskStatusFilter status, string[] expected)
    {
        var groups = await Run(
            [T("1", "open"), T("2", "done", status: TaskItemStatus.Completed), T("3", "dropped", status: TaskItemStatus.Cancelled)],
            With(f => f with { Status = status }),
            new TaskSort(TaskSortField.Title));

        Assert.Equal(expected, Titles(groups));
    }

    [Fact]
    public async Task Due_options_combine_with_or()
    {
        var tasks = new[]
        {
            T("1", "overdue", new DueDate(Today.AddDays(-1))),
            T("2", "today", new DueDate(Today)),
            T("3", "in 3 days", new DueDate(Today.AddDays(3))),
            T("4", "in 20 days", new DueDate(Today.AddDays(20))),
            T("5", "undated"),
        };

        Titles(await Run(tasks, With(f => f with { Due = new DueFilter(new HashSet<DueOption> { DueOption.Overdue, DueOption.NoDueDate }) })))
            .ShouldBe(["overdue", "undated"]);
        Titles(await Run(tasks, With(f => f with { Due = new DueFilter(new HashSet<DueOption> { DueOption.Today }) }))).ShouldBe(["today"]);
        Titles(await Run(tasks, With(f => f with { Due = new DueFilter(new HashSet<DueOption> { DueOption.Next7Days }) }))).ShouldBe(["today", "in 3 days"]);
        Titles(await Run(tasks, With(f => f with { Due = new DueFilter(new HashSet<DueOption> { DueOption.CustomRange }, Today.AddDays(2), Today.AddDays(25)) }))).ShouldBe(["in 3 days", "in 20 days"]);
    }

    [Fact]
    public async Task This_month_filter_uses_the_calendar_month()
    {
        var groups = await Run(
            [T("1", "late october", new DueDate(new DateOnly(2026, 10, 31))), T("2", "november", new DueDate(new DateOnly(2026, 11, 1)))],
            With(f => f with { Due = new DueFilter(new HashSet<DueOption> { DueOption.ThisMonth }) }));

        Assert.Equal(["late october"], Titles(groups));
    }

    [Fact]
    public async Task Priority_labels_and_text_filters_combine_with_and()
    {
        var tasks = new[]
        {
            T("1", "Write report", priority: Priority.High, labels: ["work", "urgent"], notes: "for Q3"),
            T("2", "Write email", priority: Priority.High, labels: ["work"]),
            T("3", "Buy report paper", priority: Priority.Low, labels: ["home"]),
        };

        Assert.Equal(["Write email", "Write report"], Titles(await Run(tasks, With(f => f with { Priorities = new HashSet<Priority> { Priority.High } }), new TaskSort(TaskSortField.Title))));
        Assert.Equal(["Write report"], Titles(await Run(tasks, With(f => f with { Labels = new HashSet<string> { "work", "urgent" }, AllLabels = true }))));
        Assert.Equal(["Write email", "Write report"], Titles(await Run(tasks, With(f => f with { Labels = new HashSet<string> { "URGENT", "work" }, AllLabels = false }), new TaskSort(TaskSortField.Title))));
        Assert.Equal(["Write report", "Buy report paper"], Titles(await Run(tasks, With(f => f with { Text = "REPORT" }), new TaskSort(TaskSortField.Priority, Descending: true))));
        Assert.Equal(["Write report"], Titles(await Run(tasks, With(f => f with { Text = "q3" }))));
        Assert.Equal(["Write report"], Titles(await Run(tasks, With(f => f with { Text = "report", Priorities = new HashSet<Priority> { Priority.High } }))));
    }

    [Fact]
    public async Task Collection_filter_and_hidden_collections()
    {
        var collections = new[]
        {
            new CollectionInfo("s", "c", "Work", CollectionKind.TaskList),
            new CollectionInfo("s", "other", "Other", CollectionKind.TaskList),
        };
        var store = new InMemoryStore();
        await store.SaveCollectionsAsync(new SourceInfo("s", "t", "S"), collections, TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "c", SyncResult.Snapshot<TaskItem>([T("1", "a")], new SyncState("x")), TestContext.Current.CancellationToken);
        await store.ApplyAsync("s", "other", SyncResult.Snapshot<TaskItem>([T("2", "b", collection: "other")], new SyncState("x")), TestContext.Current.CancellationToken);
        var service = new TaskListService(store, store, new FixedClock(Now));

        var only = await service.GetAsync(new TaskListQuery(With(f => f with { Collections = new HashSet<CollectionRef> { new("s", "other") } }), new TaskSort(), TaskGrouping.None, Utc), TestContext.Current.CancellationToken);
        await store.SetCollectionPreferencesAsync("s", "other", false, null, TestContext.Current.CancellationToken);
        var visible = await service.GetAsync(new TaskListQuery(TaskFilter.Default, new TaskSort(), TaskGrouping.None, Utc), TestContext.Current.CancellationToken);

        Assert.Equal(["b"], Titles(only));
        Assert.Equal(["a"], Titles(visible));
    }

    [Fact]
    public async Task Sort_is_stable_and_undated_stay_last_in_either_direction()
    {
        var tasks = new[]
        {
            T("1", "b-none"),
            T("2", "first", new DueDate(Today)),
            T("3", "a-none"),
            T("4", "second", new DueDate(Today)),
            T("5", "later", new DueDate(Today.AddDays(1))),
        };

        Assert.Equal(["first", "second", "later", "b-none", "a-none"], Titles(await Run(tasks)));
        Assert.Equal(["later", "first", "second", "b-none", "a-none"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.Due, Descending: true))));
    }

    [Fact]
    public async Task Sort_by_title_source_and_last_modified()
    {
        var t0 = Now.AddDays(-5);
        var tasks = new[] { T("1", "banana", modified: t0), T("2", "Apple", modified: t0.AddDays(2)), T("3", "cherry") };

        Assert.Equal(["Apple", "banana", "cherry"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.Title))));
        Assert.Equal(["cherry", "banana", "Apple"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.Title, true))));
        Assert.Equal(["Apple", "banana", "cherry"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.LastModified, true))));
        Assert.Equal(["banana", "Apple", "cherry"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.LastModified))));
        Assert.Equal(["banana", "Apple", "cherry"], Titles(await Run(tasks, sort: new TaskSort(TaskSortField.Source))));
    }

    [Fact]
    public async Task Due_buckets_respect_day_boundaries()
    {
        var tasks = new[]
        {
            T("1", "overdue", new DueDate(Today.AddDays(-1))),
            T("2", "earlier", new DueDate(Today.AddDays(-1)), TaskItemStatus.Completed),
            T("3", "today", new DueDate(Today)),
            T("4", "week", new DueDate(Today.AddDays(6))),
            T("5", "later", new DueDate(Today.AddDays(7))),
            T("6", "none"),
        };

        var groups = await Run(tasks, With(f => f with { Status = TaskStatusFilter.All }), grouping: TaskGrouping.DueBucket);

        Assert.Equal([DueBucket.Overdue, DueBucket.Earlier, DueBucket.Today, DueBucket.ThisWeek, DueBucket.Later, DueBucket.NoDate], groups.Select(g => g.Bucket!.Value));
        Assert.All(groups, g => Assert.Single(g.Rows));
    }

    [Fact]
    public async Task Group_by_collection_priority_and_label()
    {
        var collections = new[] { new CollectionInfo("s", "c", "Work", CollectionKind.TaskList) };
        var tasks = new[]
        {
            T("1", "a", priority: Priority.High, labels: ["x", "y"]),
            T("2", "b", priority: Priority.Low, labels: ["y"]),
            T("3", "c"),
        };

        var byCollection = await Run(tasks, grouping: TaskGrouping.Collection, collections: collections);
        var byPriority = await Run(tasks, grouping: TaskGrouping.Priority);
        var byLabel = await Run(tasks, grouping: TaskGrouping.Label);

        Assert.Equal(["Work"], byCollection.Select(g => g.Label));
        Assert.Equal([Priority.High, Priority.Low, Priority.None], byPriority.Select(g => g.Priority!.Value));
        Assert.Equal(["x", "y", string.Empty], byLabel.Select(g => g.Label));
        Assert.Equal(["a", "b"], byLabel[1].Rows.Select(r => r.Task.Title));
    }

    [Fact]
    public async Task Subtasks_nest_under_their_parent()
    {
        var groups = await Run([T("p", "parent"), T("c1", "child 1", parent: "p"), T("c2", "child 2", parent: "p"), T("g", "grandchild", parent: "c1")]);

        var parent = Assert.Single(Assert.Single(groups).Rows);
        Assert.Equal(["child 1", "child 2"], parent.Children.Select(c => c.Task.Title));
        Assert.Equal("grandchild", Assert.Single(parent.Children[0].Children).Task.Title);
    }

    [Fact]
    public async Task Subtask_whose_parent_is_filtered_out_is_shown_at_top_level()
    {
        var groups = await Run([T("p", "parent", status: TaskItemStatus.Completed), T("c", "child", parent: "p")]);

        var row = Assert.Single(Assert.Single(groups).Rows);
        Assert.Equal("child", row.Task.Title);
    }

    [Fact]
    public async Task Parent_cycles_do_not_hang_or_lose_tasks()
    {
        var groups = await Run([T("a", "a", parent: "b"), T("b", "b", parent: "a")]);

        // Each is the other's parent, so neither is a root: nothing loops, nothing is lost, nothing shows twice.
        var all = groups.SelectMany(g => g.Rows).SelectMany(Flatten).Select(r => r.Task.Title).Order().ToList();
        Assert.Equal(["a", "b"], all);
    }

    private static IEnumerable<TaskRow> Flatten(TaskRow row) => row.Children.SelectMany(Flatten).Prepend(row);

    [Fact]
    public async Task Overdue_rows_are_flagged_and_carry_collection_info()
    {
        var groups = await Run([T("1", "late", new DueDate(Today.AddDays(-1)))]);

        var row = groups[0].Rows[0];
        Assert.True(row.IsOverdue);
        Assert.Equal("Work", row.CollectionName);
        Assert.Equal("Source S", row.SourceName);
        Assert.Equal("#111111", row.Color);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var store = new InMemoryStore();
        var service = new TaskListService(store, store, new FixedClock(Now));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAsync(new TaskListQuery(TaskFilter.Default, new TaskSort(), TaskGrouping.None, Utc), cts.Token));
    }

    [Fact]
    public async Task Sample_data_flows_through_agenda_and_task_list()
    {
        var zone = TestData.Warsaw;
        var today = new DateOnly(2026, 10, 20);
        var store = await SampleData.CreateStoreAsync(today, zone, TestContext.Current.CancellationToken);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(2)));
        var agenda = new AgendaService(store, store, new AllMyTasks.Core.Calendar.FakeRecurrenceExpander(), clock);

        var entries = await agenda.GetEntriesAsync(new ViewQuery(today, today.AddDays(7), zone, new ViewFilter(), IncludeOverdue: true), TestContext.Current.CancellationToken);
        var tasks = await new TaskListService(store, store, clock).GetAsync(new TaskListQuery(TaskFilter.Default, new TaskSort(), TaskGrouping.None, zone), TestContext.Current.CancellationToken);

        Assert.Contains(entries, e => e.Title == "Design review");
        Assert.Contains(entries, e => e.Title == "Team stand-up (moved)");
        Assert.DoesNotContain(entries.Where(e => e.Start.Date == new DateTime(2026, 10, 22)), e => e.Title == "Team stand-up");
        Assert.Contains(entries, e => e is TaskEntry { IsOverdue: true });
        Assert.NotEmpty(tasks.SelectMany(g => g.Rows));
    }
}

internal static class AssertExtensions
{
    public static void ShouldBe(this string[] actual, string[] expected) => Assert.Equal(expected, actual);
}
