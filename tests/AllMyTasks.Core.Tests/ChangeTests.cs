using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Core.Tests;

/// <summary>A task source that records writes and answers them as each test scripts.</summary>
internal sealed class WritableSource(string id, params CollectionInfo[] collections) : ISource, IWritableTaskSource
{
    public string Id { get; } = id;

    public string Kind => "writable";

    public string DisplayName => "Writable";

    public List<string> Log { get; } = [];

    public Func<PendingKind, ItemKey, WriteResult> OnWrite { get; set; } = (_, _) => new WriteResult(WriteOutcome.Applied);

    public Func<SyncState?, SyncResult<TaskItem>> OnSync { get; set; } = _ => SyncResult.Snapshot<TaskItem>([], new SyncState("t"), confirmedEmpty: true);

    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollectionInfo>>(collections);

    public Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        Log.Add("sync");
        return Task.FromResult(OnSync(previous));
    }

    public Task<WriteResult> CompleteAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default) => Write(PendingKind.Complete, key);

    public Task<WriteResult> ReopenAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default) => Write(PendingKind.Reopen, key);

    private Task<WriteResult> Write(PendingKind kind, ItemKey key)
    {
        Log.Add($"{kind} {key.ItemId}");
        return Task.FromResult(OnWrite(kind, key));
    }
}

public class ChangeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static ItemKey K(string id, string source = "s") => new(source, "list", id);

    private static TaskItem Task(string id, TaskItemStatus status = TaskItemStatus.Open, string? parent = null, string source = "s") =>
        new(K(id, source), "Task " + id, Status: status, ParentKey: parent is null ? null : K(parent, source));

    private static PendingChange Change(string id, PendingKind kind, int seconds = 0, PendingState state = PendingState.Queued) =>
        new(Guid.NewGuid(), K(id), kind, T0.AddSeconds(seconds), State: state);

    // ---- PendingOverlay ----

    [Fact]
    public void Overlay_completes_a_task_and_its_subtasks_but_not_others()
    {
        var tasks = new[] { Task("p"), Task("c1", parent: "p"), Task("c2", parent: "c1"), Task("other") };

        var shown = PendingOverlay.Apply(tasks, [Change("p", PendingKind.Complete)]).ToDictionary(t => t.Key.ItemId);

        Assert.True(shown["p"].IsCompleted);
        Assert.True(shown["c1"].IsCompleted);
        Assert.True(shown["c2"].IsCompleted);
        Assert.True(shown["other"].IsOpen);
    }

    [Fact]
    public void Overlay_reopens_a_completed_task_without_touching_its_subtasks()
    {
        var tasks = new[] { Task("p", TaskItemStatus.Completed), Task("c", TaskItemStatus.Completed, parent: "p") };

        var shown = PendingOverlay.Apply(tasks, [Change("p", PendingKind.Reopen)]).ToDictionary(t => t.Key.ItemId);

        Assert.True(shown["p"].IsOpen);
        Assert.True(shown["c"].IsCompleted);
    }

    [Fact]
    public void Overlay_leaves_cancelled_tasks_and_unknown_keys_alone_and_survives_a_parent_cycle()
    {
        var tasks = new[] { Task("a", parent: "b"), Task("b", parent: "a"), Task("x", TaskItemStatus.Cancelled) };

        var shown = PendingOverlay.Apply(tasks, [Change("a", PendingKind.Complete), Change("x", PendingKind.Complete), Change("missing", PendingKind.Complete)])
            .ToDictionary(t => t.Key.ItemId);

        Assert.True(shown["a"].IsCompleted);
        Assert.True(shown["b"].IsCompleted);
        Assert.Equal(TaskItemStatus.Cancelled, shown["x"].Status);
    }

    [Fact]
    public void Overlay_uses_the_newest_change_for_a_task_and_returns_the_input_when_there_is_nothing_to_apply()
    {
        var tasks = new[] { Task("a") };

        Assert.Same(tasks, PendingOverlay.Apply(tasks, []));
        var shown = PendingOverlay.Apply(tasks, [Change("a", PendingKind.Reopen, 5), Change("a", PendingKind.Complete, 1)]);
        Assert.True(shown.Single().IsOpen);
    }

    [Fact]
    public async Task Overlay_store_shows_changes_to_readers_but_not_in_the_cache()
    {
        var store = new InMemoryStore();
        await store.ApplyAsync("s", "list", SyncResult.Snapshot<TaskItem>([Task("a")], new SyncState("t")), Ct);
        await store.SaveAsync(Change("a", PendingKind.Complete), Ct);
        var view = new PendingOverlayStore(store, store);

        Assert.True((await view.GetTasksAsync(Ct)).Single().IsCompleted);
        Assert.True((await store.GetTasksAsync(Ct)).Single().IsOpen);
    }

    // ---- TaskChangeService ----

    private sealed class ServiceRig : IDisposable
    {
        public InMemoryStore Store { get; } = new();

        public FakeProvider Provider { get; } = new();

        public TaskChangeService Service { get; }

        public List<TaskChangeEventArgs> Events { get; } = [];

        public ServiceRig()
        {
            Service = new TaskChangeService(Store, Store, Provider, new FixedClock(T0));
            Service.Changed += (_, e) => Events.Add(e);
        }

        public async Task<ISource> AddAsync(ISource source, params CollectionInfo[] collections)
        {
            Provider.Sources[source.Id] = source;
            await Store.SaveCollectionsAsync(new SourceInfo(source.Id, "k", "Source " + source.Id), collections, Ct);
            return source;
        }

        public void Dispose() => Service.Dispose();
    }

    private static CollectionInfo List(string source = "s", bool readOnly = false) => new(source, "list", "List", CollectionKind.TaskList, IsReadOnly: readOnly);

    [Fact]
    public async Task Completing_queues_one_change_and_asks_for_a_push()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new WritableSource("s"), List());

        await rig.Service.CompleteAsync(K("a"), Ct);
        await rig.Service.CompleteAsync(K("a"), Ct);

        var change = Assert.Single(await rig.Store.GetAllAsync(Ct));
        Assert.Equal((K("a"), PendingKind.Complete, PendingState.Queued, T0), (change.Key, change.Kind, change.State, change.CreatedAt));
        Assert.True(Assert.Single(rig.Events).NeedsPush);
    }

    [Fact]
    public async Task Tick_then_untick_cancels_out_and_sends_nothing()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new WritableSource("s"), List());

        await rig.Service.CompleteAsync(K("a"), Ct);
        await rig.Service.ReopenAsync(K("a"), Ct);

        Assert.Empty(await rig.Store.GetAllAsync(Ct));
        Assert.False(rig.Events[^1].NeedsPush);
    }

    [Fact]
    public async Task Untick_of_a_failed_completion_cancels_it_too()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new WritableSource("s"), List());
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete, state: PendingState.Failed), Ct);

        await rig.Service.ReopenAsync(K("a"), Ct);

        Assert.Empty(await rig.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task A_source_that_cannot_write_or_a_read_only_list_refuses_changes_and_has_no_checkbox()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new ScriptedSource("ro", SyncRig.List("ro", "list")), SyncRig.List("ro", "list"));
        await rig.AddAsync(new WritableSource("w"), List("w", readOnly: true));
        await rig.AddAsync(new WritableSource("ok"), List("ok"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CompleteAsync(K("a", "ro"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CompleteAsync(K("a", "w"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CompleteAsync(K("a", "unknown"), Ct));
        var snapshot = await rig.Service.GetSnapshotAsync(Ct);

        Assert.False(snapshot.CanChange(K("a", "ro")));
        Assert.False(snapshot.CanChange(K("a", "w")));
        Assert.True(snapshot.CanChange(K("a", "ok")));
        Assert.Empty(await rig.Store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task Discard_removes_a_change_and_retry_queues_a_failed_one_again()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new WritableSource("s"), List());
        var failed = Change("a", PendingKind.Complete, state: PendingState.Failed) with { Attempts = 3, Error = "no" };
        await rig.Store.SaveAsync(failed, Ct);
        await rig.Store.SaveAsync(Change("b", PendingKind.Complete), Ct);

        await rig.Service.RetryAsync(failed.Id, Ct);
        var retried = (await rig.Store.GetAllAsync(Ct)).Single(c => c.Id == failed.Id);
        await rig.Service.DiscardAsync(retried.Id, Ct);

        Assert.Equal((PendingState.Queued, 0, null), (retried.State, retried.Attempts, retried.Error));
        Assert.Equal("b", Assert.Single(await rig.Store.GetAllAsync(Ct)).Key.ItemId);
    }

    // ---- Engine ----

    private sealed class EngineRig : IDisposable
    {
        public InMemoryStore Store { get; } = new();

        public FakeProvider Provider { get; } = new();

        public FixedClock Clock { get; } = new(T0);

        public SyncEngine Engine { get; }

        public WritableSource Source { get; } = new("s", List());

        public EngineRig()
        {
            Engine = new SyncEngine(Store, Store, Provider, Clock, pendingChanges: Store);
            Provider.Sources["s"] = Source;
            Store.SaveSourceAsync(new SourceInfo("s", "k", "Source"), SourceConfig.Empty, Ct).GetAwaiter().GetResult();
        }

        public Task<SourceStatus> StatusAsync() => Engine.GetStatusesAsync(Ct).ContinueWith(t => t.Result.Single(), Ct, TaskContinuationOptions.None, TaskScheduler.Default);

        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public async Task The_queue_is_sent_in_order_before_the_pull_and_then_removed()
    {
        using var rig = new EngineRig();
        await rig.Store.SaveAsync(Change("b", PendingKind.Reopen, 2), Ct);
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete, 1), Ct);

        await rig.Engine.SyncSourceAsync("s", Ct);

        Assert.Equal(["Complete a", "Reopen b", "sync"], rig.Source.Log);
        Assert.Empty(await rig.Store.GetAllAsync(Ct));
        Assert.Equal(SourceStatusKind.Ok, (await rig.StatusAsync()).Kind);
    }

    [Fact]
    public async Task A_pull_between_the_tick_and_the_push_does_not_undo_the_change()
    {
        using var rig = new EngineRig();
        var view = new PendingOverlayStore(rig.Store, rig.Store);
        var old = Task("a");
        rig.Source.OnSync = _ => SyncResult.Snapshot<TaskItem>([old], new SyncState("t"));
        await rig.Engine.SyncSourceAsync("s", Ct);
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete), Ct);

        // The source is unreachable for the push, so the cache still says open while the user sees it completed.
        rig.Source.OnWrite = (_, _) => throw new SourceUnavailableException("offline");
        await rig.Engine.SyncSourceAsync("s", Ct);

        Assert.True((await rig.Store.GetTasksAsync(Ct)).Single().IsOpen);
        Assert.True((await view.GetTasksAsync(Ct)).Single().IsCompleted);
        var change = Assert.Single(await rig.Store.GetAllAsync(Ct));
        Assert.Equal((PendingState.Queued, 1), (change.State, change.Attempts));
        Assert.Equal(SourceStatusKind.Offline, (await rig.StatusAsync()).Kind);
    }

    [Fact]
    public async Task Rejected_credentials_keep_the_change_queued()
    {
        using var rig = new EngineRig();
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete), Ct);
        rig.Source.OnWrite = (_, _) => throw new SourceAuthException("no");

        await rig.Engine.SyncSourceAsync("s", Ct);

        Assert.Equal(PendingState.Queued, Assert.Single(await rig.Store.GetAllAsync(Ct)).State);
        Assert.Equal(SourceStatusKind.NeedsAttention, (await rig.StatusAsync()).Kind);
    }

    [Fact]
    public async Task A_refused_change_is_marked_failed_and_the_rest_still_go()
    {
        using var rig = new EngineRig();
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete, 1), Ct);
        await rig.Store.SaveAsync(Change("b", PendingKind.Complete, 2), Ct);
        rig.Source.OnWrite = (_, key) => key.ItemId == "a" ? throw new SourceException("Todoist refused the change.") : new WriteResult(WriteOutcome.Applied);

        await rig.Engine.SyncSourceAsync("s", Ct);

        var left = Assert.Single(await rig.Store.GetAllAsync(Ct));
        Assert.Equal(("a", PendingState.Failed, "Todoist refused the change."), (left.Key.ItemId, left.State, left.Error));
        Assert.Contains("sync", rig.Source.Log);
        Assert.Equal(SourceStatusKind.Ok, (await rig.StatusAsync()).Kind);
    }

    [Fact]
    public async Task A_task_deleted_remotely_drops_the_change_and_says_so()
    {
        using var rig = new EngineRig();
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete), Ct);
        rig.Source.OnWrite = (_, _) => new WriteResult(WriteOutcome.NotFound);

        await rig.Engine.SyncSourceAsync("s", Ct);

        Assert.Empty(await rig.Store.GetAllAsync(Ct));
        Assert.Contains("no longer exists", (await rig.StatusAsync()).Warning);
    }

    [Fact]
    public async Task Changes_for_a_source_that_stopped_allowing_them_become_failed()
    {
        using var rig = new EngineRig();
        rig.Provider.Sources["s"] = new ScriptedSource("s", SyncRig.List("s", "list")) { OnTasks = (_, _) => System.Threading.Tasks.Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("t"), confirmedEmpty: true)) };
        await rig.Store.SaveAsync(Change("a", PendingKind.Complete), Ct);

        await rig.Engine.SyncSourceAsync("s", Ct);

        var change = Assert.Single(await rig.Store.GetAllAsync(Ct));
        Assert.Equal(PendingState.Failed, change.State);
        Assert.Contains("switched off", change.Error);
    }

    // ---- Views ----

    [Fact]
    public async Task The_list_and_details_carry_the_checkbox_and_pending_state()
    {
        using var rig = new ServiceRig();
        await rig.AddAsync(new WritableSource("s"), List());
        await rig.Store.ApplyAsync("s", "list", SyncResult.Snapshot<TaskItem>([Task("a"), Task("b")], new SyncState("t")), Ct);
        await rig.Service.CompleteAsync(K("b"), Ct);
        var view = new PendingOverlayStore(rig.Store, rig.Store);
        var clock = new FixedClock(T0);

        var groups = await new TaskListService(view, rig.Store, clock, rig.Service).GetAsync(
            new TaskListQuery(TaskFilter.Default with { Status = TaskStatusFilter.All }, new TaskSort(TaskSortField.Title), TaskGrouping.None, TimeZoneInfo.Utc), Ct);
        var rows = groups.Single().Rows.ToDictionary(r => r.Task.Key.ItemId);
        var details = await new TaskDetailsService(view, rig.Store, clock, rig.Service).GetAsync(K("b"), TimeZoneInfo.Utc, Ct);

        Assert.True(rows["a"].CanChange);
        Assert.Null(rows["a"].Pending);
        Assert.True(rows["b"].Task.IsCompleted);
        Assert.Equal(PendingKind.Complete, rows["b"].Pending!.Kind);
        Assert.True(details!.CanChange);
        Assert.True(details.Task.IsCompleted);
        Assert.NotNull(details.Pending);
    }
}
