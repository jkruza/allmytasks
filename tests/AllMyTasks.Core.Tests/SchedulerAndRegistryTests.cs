using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;
using static AllMyTasks.Core.Tests.SyncRig;

namespace AllMyTasks.Core.Tests;

public class BackoffTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Doubles_up_to_thirty_minutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), Backoff.Delay(failures));

    [Theory]
    [InlineData(1, null, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 10, 10)]
    [InlineData(6, 10, 30)]
    [InlineData(1, 600, 60)]
    public void A_server_wait_raises_the_delay_but_never_lowers_it_and_is_capped_at_an_hour(int failures, int? serverMinutes, int expectedMinutes) =>
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            Backoff.Delay(failures, serverMinutes is { } m ? TimeSpan.FromMinutes(m) : null));
}

public class SyncSchedulerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 15)]
    [InlineData("5", 5)]
    [InlineData("0", 1)]
    [InlineData("100000", 1440)]
    [InlineData("abc", 15)]
    public void Interval_comes_from_config_with_a_default_and_a_minimum(string? value, int minutes)
    {
        var config = value is null ? SourceConfig.Empty : new SourceConfig(new Dictionary<string, string> { [SyncScheduler.IntervalKey] = value });

        Assert.Equal(TimeSpan.FromMinutes(minutes), SyncScheduler.IntervalFor(config));
    }

    [Fact]
    public void Due_rules_follow_status()
    {
        var interval = TimeSpan.FromMinutes(15);

        Assert.True(SyncScheduler.IsDue(SourceStatus.NewFor("s"), interval, Now));
        Assert.False(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.Ok, Now.AddMinutes(-14)), interval, Now));
        Assert.True(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.Ok, Now.AddMinutes(-15)), interval, Now));
        Assert.False(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.Syncing, Now.AddHours(-9)), interval, Now));
        Assert.False(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.NeedsAttention, Now.AddHours(-9)), interval, Now));
        Assert.False(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.Offline, Now.AddHours(-9), NextRetryAt: Now.AddMinutes(2)), interval, Now));
        Assert.True(SyncScheduler.IsDue(new SourceStatus("s", SourceStatusKind.Error, Now.AddHours(-9), NextRetryAt: Now.AddMinutes(-1)), interval, Now));
    }

    [Fact]
    public async Task Tick_syncs_only_the_sources_that_are_due()
    {
        using var rig = new SyncRig();
        var fresh = await rig.AddAsync("fresh");
        var stale = await rig.AddAsync("stale");
        var fast = await rig.AddAsync("fast");
        await rig.Store.SaveSourceAsync(new SourceInfo("fast", "scripted", "Fast"), new SourceConfig(new Dictionary<string, string> { [SyncScheduler.IntervalKey] = "2" }), Ct);
        foreach (var s in new[] { fresh, stale, fast })
        {
            s.OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("s"), confirmedEmpty: true));
        }

        using var scheduler = new SyncScheduler(rig.Engine, rig.Store, rig.Clock);
        await scheduler.TickAsync(Ct);
        Assert.Equal([1, 1, 1], [fresh.Calls, stale.Calls, fast.Calls]);

        rig.Clock.Now = rig.Clock.Now.AddMinutes(3);
        await scheduler.TickAsync(Ct);
        Assert.Equal([1, 1, 2], [fresh.Calls, stale.Calls, fast.Calls]);

        rig.Clock.Now = rig.Clock.Now.AddMinutes(13);
        await scheduler.OnWindowActivatedAsync(Ct);
        Assert.Equal([2, 2, 3], [fresh.Calls, stale.Calls, fast.Calls]);
    }

    [Fact]
    public async Task Tick_respects_backoff_and_leaves_sources_that_need_the_user_alone()
    {
        using var rig = new SyncRig();
        var offline = await rig.AddAsync("offline");
        var locked = await rig.AddAsync("locked");
        offline.OnTasks = (_, _) => throw new SourceUnavailableException();
        locked.OnTasks = (_, _) => throw new SourceAuthException();
        using var scheduler = new SyncScheduler(rig.Engine, rig.Store, rig.Clock);
        await scheduler.TickAsync(Ct);

        rig.Clock.Now = rig.Clock.Now.AddSeconds(30);
        await scheduler.TickAsync(Ct);
        Assert.Equal([1, 1], [offline.Calls, locked.Calls]);

        rig.Clock.Now = rig.Clock.Now.AddMinutes(2);
        await scheduler.TickAsync(Ct);
        Assert.Equal([2, 1], [offline.Calls, locked.Calls]);
    }
}

public class SourceRegistryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class FeedFactory : ISourceFactory
    {
        public string Kind => "feed";

        public string DisplayName => "Feed";

        public SourceConfigSchema ConfigSchema { get; } = new(
        [
            new SourceConfigField("url", "Feed address", FieldKind.Url, Required: true, IsSecret: true),
            new SourceConfigField("user", "User name", FieldKind.Text),
            new SourceConfigField("password", "Password", FieldKind.Password),
            new SourceConfigField("refreshMinutes", "Refresh every (minutes)", FieldKind.Number),
        ]);

        public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets) =>
            new ScriptedSource(sourceId, List(sourceId));
    }

    private static (SourceRegistry Registry, InMemoryStore Store, InMemorySecretStore Secrets) Create()
    {
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        return (new SourceRegistry([new FeedFactory()], store, secrets), store, secrets);
    }

    private static Dictionary<string, string> Values(string url = "https://example.com/a.ics", string user = "me", string password = "pw", string refresh = "10") =>
        new() { ["url"] = url, ["user"] = user, ["password"] = password, ["refreshMinutes"] = refresh };

    [Fact]
    public void Validation_reports_missing_and_malformed_fields()
    {
        var factory = new FeedFactory();

        Assert.Empty(SourceRegistry.Validate(factory, "Name", Values()));
        Assert.Contains(SourceRegistry.Validate(factory, " ", Values()), e => e.Key == "name");
        Assert.Contains(SourceRegistry.Validate(factory, "n", Values(url: "")), e => e.Key == "url");
        Assert.Contains(SourceRegistry.Validate(factory, "n", Values(url: "ftp://x")), e => e.Key == "url");
        Assert.Contains(SourceRegistry.Validate(factory, "n", Values(url: "not a url")), e => e.Key == "url");
        Assert.Contains(SourceRegistry.Validate(factory, "n", Values(refresh: "soon")), e => e.Key == "refreshMinutes");
        Assert.Empty(SourceRegistry.Validate(factory, "n", Values(url: "webcal://example.com/a.ics")));
    }

    [Fact]
    public async Task Add_sends_secret_fields_to_the_secret_store_and_the_rest_to_config()
    {
        var (registry, store, secrets) = Create();

        var info = await registry.AddAsync("feed", " My feed ", Values(), Ct);

        Assert.Equal("My feed", info.DisplayName);
        Assert.Equal("https://example.com/a.ics", await secrets.GetAsync(info.Id, "url", Ct));
        Assert.Equal("pw", await secrets.GetAsync(info.Id, "password", Ct));
        var config = (await store.GetSourceConfigAsync(info.Id, Ct))!;
        Assert.Equal(["refreshMinutes", "user"], config.Values.Keys.Order());
        Assert.DoesNotContain("pw", config.Values.Values);
        Assert.DoesNotContain("https://example.com/a.ics", config.Values.Values);
    }

    [Fact]
    public async Task Add_rejects_invalid_input_and_unknown_kinds()
    {
        var (registry, store, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync("feed", "n", Values(url: ""), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync("nope", "n", Values(), Ct));
        Assert.Empty(await store.GetSourcesAsync(Ct));
    }

    [Fact]
    public async Task Edit_keeps_secrets_left_blank_and_replaces_the_ones_given()
    {
        var (registry, _, secrets) = Create();
        var info = await registry.AddAsync("feed", "Feed", Values(), Ct);

        await registry.UpdateAsync(info.Id, "Renamed", Values(url: "", password: "new-pw", user: "you"), Ct);

        Assert.Equal("https://example.com/a.ics", await secrets.GetAsync(info.Id, "url", Ct));
        Assert.Equal("new-pw", await secrets.GetAsync(info.Id, "password", Ct));
        var source = await registry.GetAsync(new SourceInfo(info.Id, "feed", "Renamed"), Ct);
        Assert.Equal(info.Id, source.Id);
    }

    [Fact]
    public async Task Remove_deletes_the_source_its_data_and_its_secrets_together()
    {
        var (registry, store, secrets) = Create();
        var info = await registry.AddAsync("feed", "Feed", Values(), Ct);
        await store.ApplyAsync(info.Id, "list", SyncResult.Snapshot<TaskItem>([Item(info.Id, "1")], new SyncState("s")), Ct);

        await registry.RemoveAsync(info.Id, Ct);

        Assert.Empty(await store.GetSourcesAsync(Ct));
        Assert.Empty(await store.GetTasksAsync(Ct));
        Assert.Null(await secrets.GetAsync(info.Id, "url", Ct));
        Assert.Null(await secrets.GetAsync(info.Id, "password", Ct));
    }

    [Fact]
    public async Task Instances_are_cached_until_invalidated_and_unknown_kinds_fail_clearly()
    {
        var (registry, _, _) = Create();
        var info = new SourceInfo("feed-1", "feed", "Feed");

        var a = await registry.GetAsync(info, Ct);
        var b = await registry.GetAsync(info, Ct);
        registry.Invalidate("feed-1");
        var c = await registry.GetAsync(info, Ct);

        Assert.Same(a, b);
        Assert.NotSame(a, c);
        await Assert.ThrowsAsync<SourceException>(() => registry.GetAsync(new SourceInfo("x", "gone", "X"), Ct));
    }
}

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(-30, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(24 * 3600, "yesterday")]
    [InlineData(3 * 24 * 3600, "3 days ago")]
    public void Formats_short_ages(int secondsAgo, string expected) =>
        Assert.Equal(expected, AllMyTasks.Core.Views.RelativeTime.Ago(Now.AddSeconds(-secondsAgo), Now));
}
