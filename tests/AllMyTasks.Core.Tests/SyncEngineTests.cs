using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;
using static AllMyTasks.Core.Tests.SyncRig;

namespace AllMyTasks.Core.Tests;

public class SyncEngineTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task<SyncResult<TaskItem>> Snapshot(string source, params string[] ids) =>
        Task.FromResult(SyncResult.Snapshot<TaskItem>([.. ids.Select(i => Item(source, i))], new SyncState("s1")));

    [Fact]
    public async Task First_sync_stores_a_snapshot_records_state_and_status_and_raises_the_event()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Snapshot("src", "1", "2");
        var raised = new List<string>();
        rig.Engine.SourceSynced += (_, e) => raised.Add(e.SourceId);

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(["Task 1", "Task 2"], (await rig.Store.GetTasksAsync(Ct)).Select(t => t.Title).Order());
        Assert.Null(source.TaskCalls.Single());
        Assert.Equal("s1", (await rig.Store.GetSyncStateAsync("src", "list", Ct))!.Token);
        var status = await rig.StatusAsync("src");
        Assert.Equal((SourceStatusKind.Ok, rig.Clock.Now), (status.Kind, status.LastSyncedAt));
        Assert.Equal(["src"], raised);
    }

    [Fact]
    public async Task Later_syncs_pass_the_previous_state_and_apply_deltas()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, previous) => previous is null
            ? Snapshot("src", "1", "2")
            : Task.FromResult(SyncResult.Delta([Item("src", "3")], ["1"], new SyncState("s2")));

        await rig.Engine.SyncSourceAsync("src", Ct);
        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(["Task 2", "Task 3"], (await rig.Store.GetTasksAsync(Ct)).Select(t => t.Title).Order());
        Assert.Equal("s1", source.TaskCalls[1]!.Token);
        Assert.Equal("s2", (await rig.Store.GetSyncStateAsync("src", "list", Ct))!.Token);
    }

    [Fact]
    public async Task An_expired_state_is_retried_once_from_scratch()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        await rig.Store.SaveCollectionsAsync(new SourceInfo("src", "scripted", "S"), [List("src")], Ct);
        await rig.Store.SaveSyncStateAsync("src", "list", new SyncState("old"), Ct);
        source.OnTasks = (_, previous) => previous is null ? Snapshot("src", "1") : throw new SyncStateExpiredException();

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(["old", null], source.TaskCalls.Select(s => s?.Token));
        Assert.Equal(SourceStatusKind.Ok, (await rig.StatusAsync("src")).Kind);
        Assert.Single(await rig.Store.GetTasksAsync(Ct));
    }

    [Fact]
    public async Task A_second_expiry_is_an_error_not_a_loop()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        await rig.Store.SaveCollectionsAsync(new SourceInfo("src", "scripted", "S"), [List("src")], Ct);
        await rig.Store.SaveSyncStateAsync("src", "list", new SyncState("old"), Ct);
        source.OnTasks = (_, _) => throw new SyncStateExpiredException();

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(2, source.Calls);
        Assert.Equal(SourceStatusKind.Error, (await rig.StatusAsync("src")).Kind);
    }

    [Fact]
    public async Task Auth_failure_needs_attention_keeps_the_cache_and_schedules_no_retry()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Snapshot("src", "1");
        await rig.Engine.SyncSourceAsync("src", Ct);
        source.OnTasks = (_, _) => throw new SourceAuthException("password hunter2 rejected");

        await rig.Engine.SyncSourceAsync("src", Ct);

        var status = await rig.StatusAsync("src");
        Assert.Equal(SourceStatusKind.NeedsAttention, status.Kind);
        Assert.Null(status.NextRetryAt);
        Assert.DoesNotContain("hunter2", status.LastError, StringComparison.Ordinal);
        Assert.Single(await rig.Store.GetTasksAsync(Ct));
        Assert.NotNull(status.LastSyncedAt);
    }

    [Fact]
    public async Task Reset_attention_after_an_edit_lets_the_source_sync_again()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => throw new SourceAuthException();
        await rig.Engine.SyncSourceAsync("src", Ct);

        await rig.Engine.ResetAttentionAsync("src", Ct);

        var status = await rig.StatusAsync("src");
        Assert.Equal(SourceStatusKind.New, status.Kind);
        Assert.True(SyncScheduler.IsDue(status, TimeSpan.FromMinutes(15), rig.Clock.Now));
    }

    [Fact]
    public async Task Network_failures_go_offline_keep_the_cache_and_back_off_exponentially()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Snapshot("src", "1");
        await rig.Engine.SyncSourceAsync("src", Ct);
        source.OnTasks = (_, _) => throw new SourceUnavailableException("dns");

        var retryDelays = new List<TimeSpan>();
        for (var i = 0; i < 7; i++)
        {
            await rig.Engine.SyncSourceAsync("src", Ct);
            var s = await rig.StatusAsync("src");
            retryDelays.Add(s.NextRetryAt!.Value - rig.Clock.Now);
        }

        Assert.Equal([1, 2, 4, 8, 16, 30, 30], retryDelays.Select(d => (int)d.TotalMinutes));
        Assert.Equal(SourceStatusKind.Offline, (await rig.StatusAsync("src")).Kind);
        Assert.Single(await rig.Store.GetTasksAsync(Ct));
    }

    [Fact]
    public async Task A_success_clears_failures_and_backoff()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => throw new SourceUnavailableException();
        await rig.Engine.SyncSourceAsync("src", Ct);
        await rig.Engine.SyncSourceAsync("src", Ct);
        source.OnTasks = (_, _) => Snapshot("src", "1");

        await rig.Engine.SyncSourceAsync("src", Ct);

        var status = await rig.StatusAsync("src");
        Assert.Equal((SourceStatusKind.Ok, 0, null), (status.Kind, status.ConsecutiveFailures, status.NextRetryAt));
    }

    [Fact]
    public async Task Other_failures_are_errors_with_generic_text_and_contract_breaches_do_not_escape()
    {
        using var rig = new SyncRig();
        var broken = await rig.AddAsync("broken");
        var buggy = await rig.AddAsync("buggy");
        broken.OnTasks = (_, _) => throw new SourceDataException("secret=abc in body");
        buggy.OnTasks = (_, _) => throw new InvalidOperationException("token=xyz");

        await rig.Engine.SyncAllAsync(Ct);

        var b = await rig.StatusAsync("broken");
        var g = await rig.StatusAsync("buggy");
        Assert.Equal((SourceStatusKind.Error, SourceStatusKind.Error), (b.Kind, g.Kind));
        Assert.DoesNotContain("secret=abc", b.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("token=xyz", g.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_provider_kind_is_an_error_status()
    {
        using var rig = new SyncRig();
        await rig.Store.SaveSourceAsync(new SourceInfo("ghost", "vanished", "Ghost"), SourceConfig.Empty, Ct);

        await rig.Engine.SyncSourceAsync("ghost", Ct);

        Assert.Equal(SourceStatusKind.Error, (await rig.StatusAsync("ghost")).Kind);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_source_join_a_single_sync()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        var release = new TaskCompletionSource();
        source.OnCollections = () => release.Task;
        source.OnTasks = (_, _) => Snapshot("src", "1");

        var first = rig.Engine.SyncSourceAsync("src", Ct);
        var second = rig.Engine.SyncSourceAsync("src", Ct);
        Assert.True(rig.Engine.IsSyncing);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, source.Calls);
        Assert.False(rig.Engine.IsSyncing);
    }

    [Fact]
    public async Task Sources_sync_in_parallel_up_to_the_cap()
    {
        using var rig = new SyncRig(maxParallel: 2);
        var active = 0;
        var peak = 0;
        for (var i = 0; i < 5; i++)
        {
            var id = "s" + i;
            var source = await rig.AddAsync(id);
            source.OnTasks = async (_, _) =>
            {
                var now = Interlocked.Increment(ref active);
                int seen;
                do
                {
                    seen = Volatile.Read(ref peak);
                }
                while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
                await Task.Delay(30, Ct);
                Interlocked.Decrement(ref active);
                return SyncResult.Snapshot<TaskItem>([], new SyncState("x"), confirmedEmpty: true);
            };
        }

        await rig.Engine.SyncAllAsync(Ct);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task An_unconfirmed_empty_snapshot_does_not_wipe_a_well_filled_cache()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Snapshot("src", "1", "2", "3", "4", "5", "6");
        await rig.Engine.SyncSourceAsync("src", Ct);
        var raised = 0;
        rig.Engine.SourceSynced += (_, _) => raised++;
        source.OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("bad")));

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(6, (await rig.Store.GetTasksAsync(Ct)).Count);
        Assert.Equal("s1", (await rig.Store.GetSyncStateAsync("src", "list", Ct))!.Token);
        var status = await rig.StatusAsync("src");
        Assert.Equal(SourceStatusKind.Ok, status.Kind);
        Assert.Contains("kept", status.Warning, StringComparison.Ordinal);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_confirmed_empty_snapshot_and_small_caches_are_applied()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Snapshot("src", "1", "2", "3", "4", "5", "6");
        await rig.Engine.SyncSourceAsync("src", Ct);
        source.OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("empty"), confirmedEmpty: true));
        await rig.Engine.SyncSourceAsync("src", Ct);
        Assert.Empty(await rig.Store.GetTasksAsync(Ct));

        source.OnTasks = (_, _) => Snapshot("src", "1", "2");
        await rig.Engine.SyncSourceAsync("src", Ct);
        source.OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("e2")));
        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Empty(await rig.Store.GetTasksAsync(Ct));
        Assert.Null((await rig.StatusAsync("src")).Warning);
    }

    [Fact]
    public async Task Skipped_items_are_counted_from_warnings()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([Item("src", "1")], new SyncState("s"), [new SyncWarning("x", "bad"), new SyncWarning("y", "bad")]));

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(2, (await rig.StatusAsync("src")).SkippedItems);
    }

    [Fact]
    public async Task Hidden_collections_are_not_synced()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync("src", List("src", "shown"), List("src", "hidden"));
        await rig.Store.SaveCollectionsAsync(new SourceInfo("src", "scripted", "S"), [List("src", "shown"), List("src", "hidden")], Ct);
        await rig.Store.SetCollectionPreferencesAsync("src", "hidden", false, null, Ct);
        var seen = new List<string>();
        source.OnTasks = (c, _) =>
        {
            seen.Add(c.Id);
            return Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("s"), confirmedEmpty: true));
        };

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.Equal(["shown"], seen);
    }

    [Fact]
    public async Task Status_survives_a_restart_so_stale_data_is_never_shown_as_fresh()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => throw new SourceUnavailableException();
        await rig.Engine.SyncSourceAsync("src", Ct);

        using var restarted = new SyncEngine(rig.Store, rig.Store, rig.Provider, rig.Clock);
        var status = (await restarted.GetStatusesAsync(Ct)).Single();

        Assert.Equal((SourceStatusKind.Offline, 1), (status.Kind, status.ConsecutiveFailures));
    }

    [Fact]
    public async Task Cancellation_propagates_and_restores_the_previous_status()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        using var cts = new CancellationTokenSource();
        source.OnTasks = async (_, _) =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return null!;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Engine.SyncSourceAsync("src", cts.Token));

        Assert.Equal(SourceStatusKind.New, (await rig.StatusAsync("src")).Kind);
        Assert.False(rig.Engine.IsSyncing);
    }

    [Fact]
    public async Task Calendar_collections_go_to_calendar_sources_only()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync("src", new CollectionInfo("src", "cal", "Cal", CollectionKind.Calendar));
        var asked = false;
        source.OnEvents = (_, _) =>
        {
            asked = true;
            return Task.FromResult(SyncResult.Snapshot<AllMyTasks.Core.Calendar.CalendarEvent>([], new SyncState("s"), confirmedEmpty: true));
        };

        await rig.Engine.SyncSourceAsync("src", Ct);

        Assert.True(asked);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Sample_source_syncs_through_the_engine_and_regenerates_on_a_new_day()
    {
        using var rig = new SyncRig();
        var zone = TimeZoneInfo.Utc;
        var factory = new AllMyTasks.Core.Samples.SampleSourceFactory(rig.Clock, zone);
        var info = new SourceInfo("sample-1", factory.Kind, "Samples");
        await rig.Store.SaveSourceAsync(info, SourceConfig.Empty, Ct);
        rig.Provider.Sources["sample-1"] = factory.Create("sample-1", "Samples", SourceConfig.Empty, new AllMyTasks.Core.Storage.InMemorySecretStore());

        await rig.Engine.SyncSourceAsync("sample-1", Ct);
        var first = (await rig.Store.GetTasksAsync(Ct)).Count;
        var stateToday = await rig.Store.GetSyncStateAsync("sample-1", "todo", Ct);
        await rig.Engine.SyncSourceAsync("sample-1", Ct);
        rig.Clock.Now = rig.Clock.Now.AddDays(1);
        await rig.Engine.SyncSourceAsync("sample-1", Ct);

        Assert.True(first > 5);
        Assert.Equal(first, (await rig.Store.GetTasksAsync(Ct)).Count);
        Assert.NotEqual(stateToday, await rig.Store.GetSyncStateAsync("sample-1", "todo", Ct));
        Assert.Equal(4, (await rig.Store.GetCollectionsAsync(Ct)).Count);
    }
}
