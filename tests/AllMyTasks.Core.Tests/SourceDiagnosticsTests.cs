using AllMyTasks.Core.Diagnostics;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Core.Tests;

public class SourceDiagnosticsTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly ReportEnvironment Environment = new("0.9.0-alpha", "Linux 6.1", "10.0.1");

    [Fact]
    public async Task A_successful_full_sync_is_recorded_with_its_strategy()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (c, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([SyncRig.Item("src", "1")], new SyncState("t1")));

        await rig.Engine.SyncSourceAsync("src", Ct);

        var attempt = rig.Engine.GetLastAttempt("src")!;
        Assert.True(attempt.Succeeded);
        Assert.Equal(rig.Clock.Now, attempt.StartedAt);
        Assert.Equal(1, attempt.FullCollections);
        Assert.Equal(0, attempt.DeltaCollections);
    }

    [Fact]
    public async Task A_later_delta_sync_changes_the_strategy()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (c, previous) => Task.FromResult(previous is null
            ? SyncResult.Snapshot<TaskItem>([SyncRig.Item("src", "1")], new SyncState("t1"))
            : SyncResult.Delta<TaskItem>([], [], new SyncState("t2")));

        await rig.Engine.SyncSourceAsync("src", Ct);
        await rig.Engine.SyncSourceAsync("src", Ct);

        var attempt = rig.Engine.GetLastAttempt("src")!;
        Assert.Equal(0, attempt.FullCollections);
        Assert.Equal(1, attempt.DeltaCollections);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failures_get_a_fixed_category(Exception failure, ErrorCategory expected)
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (_, _) => throw failure;

        await rig.Engine.SyncSourceAsync("src", Ct);

        var attempt = rig.Engine.GetLastAttempt("src")!;
        Assert.False(attempt.Succeeded);
        Assert.Equal(expected, attempt.Category);
    }

    public static TheoryData<Exception, ErrorCategory> Failures() => new()
    {
        { new SourceAuthException("x"), ErrorCategory.Authentication },
        { new SourceUnavailableException("x"), ErrorCategory.Offline },
        { new SourceUnavailableException("x") { RetryAfter = TimeSpan.FromMinutes(5) }, ErrorCategory.RateLimit },
        { new SourceDataException("x"), ErrorCategory.ServerError },
        { new InvalidOperationException("x"), ErrorCategory.Unexpected },
    };

    [Fact]
    public async Task A_removed_source_forgets_its_attempt()
    {
        using var rig = new SyncRig();
        var source = await rig.AddAsync();
        source.OnTasks = (c, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([], new SyncState("t"), confirmedEmpty: true));
        await rig.Engine.SyncSourceAsync("src", Ct);

        rig.Engine.Forget("src");

        Assert.Null(rig.Engine.GetLastAttempt("src"));
    }

    [Fact]
    public async Task The_service_counts_items_by_collection_and_keeps_hidden_ones_marked()
    {
        using var rig = new SyncRig();
        var shown = SyncRig.List("src", "a");
        var hidden = SyncRig.List("src", "b");
        var source = await rig.AddAsync("src", shown, hidden);
        await rig.Store.SetCollectionPreferencesAsync("src", "b", false, null, Ct);
        source.OnTasks = (c, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([SyncRig.Item("src", "1", c.Id), SyncRig.Item("src", "2", c.Id)], new SyncState("t")));

        await rig.Engine.SyncSourceAsync("src", Ct);
        var found = await new SourceDiagnosticsService(rig.Engine, rig.Store, rig.Store).GetAsync(Ct);

        var diagnostics = Assert.Single(found);
        Assert.Equal(SourceStatusKind.Ok, diagnostics.Status);
        Assert.Equal(2, diagnostics.Collections.Count);
        Assert.Equal(2, diagnostics.Collections.Single(c => c.IsVisible).Items);
        Assert.Equal(0, diagnostics.Collections.Single(c => !c.IsVisible).Items);
        Assert.Equal("full", diagnostics.Strategy);
    }

    [Theory]
    [InlineData("https://gitlab.example.com/a/b?token=MARKER", "gitlab.example.com")]
    [InlineData("https://user:MARKER@Git.Example.com:8443/x", "git.example.com")]
    [InlineData("http://192.168.1.5/dav", "192.168.1.5")]
    [InlineData("not an address", null)]
    [InlineData("ftp://x.example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Addresses_are_reduced_to_the_host(string? address, string? expected) =>
        Assert.Equal(expected, SourceDiagnosticsService.HostOnly(address));

    [Fact]
    public async Task The_report_never_contains_secrets_names_addresses_or_item_text()
    {
        using var rig = new SyncRig();
        var store = rig.Store;
        var info = new SourceInfo("MARKER_ID", "gitlab", "MARKER_NAME");
        var config = new SourceConfig(new Dictionary<string, string>
        {
            ["baseUrl"] = "https://gitlab.example.com/MARKER_PATH?private_token=MARKER_SECRET",
            ["token"] = "MARKER_TOKEN",
            ["user"] = "MARKER_USER",
        });
        await store.SaveSourceAsync(info, config, Ct);
        var collection = new CollectionInfo("MARKER_ID", "c", "MARKER_COLLECTION", CollectionKind.TaskList);
        var source = new ScriptedSource("MARKER_ID", collection)
        {
            OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([new TaskItem(new ItemKey("MARKER_ID", "c", "i"), "MARKER_TITLE") { Notes = "MARKER_NOTES" }], new SyncState("MARKER_STATE"))),
        };
        rig.Provider.Sources["MARKER_ID"] = source;
        await rig.Engine.SyncSourceAsync("MARKER_ID", Ct);
        source.OnTasks = (_, _) => throw new SourceUnavailableException("MARKER_MESSAGE https://MARKER_HOST/x") { Diagnostics = "MARKER_DETAIL" };
        await rig.Engine.SyncSourceAsync("MARKER_ID", Ct);

        var found = await new SourceDiagnosticsService(rig.Engine, store, store).GetAsync(Ct);
        var report = DiagnosticsReportBuilder.Build(Environment, found, rig.Clock.Now);

        Assert.DoesNotContain("MARKER", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Host: gitlab.example.com", report, StringComparison.Ordinal);
        Assert.Contains("Source gitlab src-", report, StringComparison.Ordinal);
        Assert.Contains("Error category: offline or server unavailable", report, StringComparison.Ordinal);
        Assert.Contains("1 tasks", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_describes_each_status()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var never = new SourceDiagnostics("src-000001", "todoist", SourceStatusKind.New, null, 0, null, 0, null, null, []);
        var failing = new SourceDiagnostics(
            "src-000002",
            "github",
            SourceStatusKind.Offline,
            now.AddHours(-3),
            4,
            now.AddMinutes(15),
            2,
            new SyncAttempt(now.AddMinutes(-1), TimeSpan.FromSeconds(1.234), ErrorCategory.Offline, 0, 0),
            null,
            [new CollectionDiagnostics(CollectionKind.Calendar, true, 12)]);

        var report = DiagnosticsReportBuilder.Build(Environment, [failing, never], now);

        Assert.Equal(
            """
            All My Tasks diagnostics
            App: 0.9.0-alpha
            OS: Linux 6.1
            .NET: 10.0.1
            Created: 2026-11-11 12:00:00Z
            Sources: 2

            Source github src-000002
              Status: offline
              Last successful sync: 2026-11-11 09:00:00Z (3 h ago)
              Last attempt: 2026-11-11 11:59:00Z, took 1.2 s
              Sync strategy: unknown
              Error category: offline or server unavailable
              Consecutive failures: 4
              Next retry: 2026-11-11 12:15:00Z
              Items skipped in last sync: 2
              Collections: 1 (1 shown)
                calendar: 12 events

            Source todoist src-000001
              Status: not synced yet
              Last successful sync: never
              Last attempt: none since the app started
              Sync strategy: unknown
              Error category: none
              Consecutive failures: 0
              Next retry: none
              Items skipped in last sync: 0
              Collections: 0 (0 shown)

            """.ReplaceLineEndings("\n"),
            report);
    }

    [Fact]
    public void Order_is_stable_whatever_the_input_order()
    {
        var now = DateTimeOffset.UnixEpoch;
        var a = new SourceDiagnostics("src-aaaaaa", "google", SourceStatusKind.Ok, null, 0, null, 0, null, null, [new(CollectionKind.TaskList, true, 1), new(CollectionKind.Calendar, true, 5)]);
        var b = new SourceDiagnostics("src-bbbbbb", "google", SourceStatusKind.Ok, null, 0, null, 0, null, null, []);
        var c = new SourceDiagnostics("src-cccccc", "caldav", SourceStatusKind.Ok, null, 0, null, 0, null, null, []);

        Assert.Equal(
            DiagnosticsReportBuilder.Build(Environment, [a, b, c], now),
            DiagnosticsReportBuilder.Build(Environment, [c, b, a], now));
        Assert.True(DiagnosticsReportBuilder.Build(Environment, [a, b, c], now).IndexOf("caldav", StringComparison.Ordinal) < DiagnosticsReportBuilder.Build(Environment, [a, b, c], now).IndexOf("google", StringComparison.Ordinal));
    }

    [Fact]
    public void Short_ids_are_stable_short_and_hide_the_source_id()
    {
        var id = SourceDiagnosticsService.ShortId("0d9f1c3e-0000-4000-8000-000000000001");

        Assert.Equal(id, SourceDiagnosticsService.ShortId("0d9f1c3e-0000-4000-8000-000000000001"));
        Assert.NotEqual(id, SourceDiagnosticsService.ShortId("0d9f1c3e-0000-4000-8000-000000000002"));
        Assert.Matches("^src-[0-9a-f]{6}$", id);
    }

    [Fact]
    public void After_a_restart_the_category_comes_from_the_saved_status()
    {
        var offline = new SourceDiagnostics("s", "k", SourceStatusKind.Offline, null, 1, null, 0, null, null, []);
        var attention = offline with { Status = SourceStatusKind.NeedsAttention };
        var ok = offline with { Status = SourceStatusKind.Ok };

        Assert.Equal(ErrorCategory.Offline, offline.Category);
        Assert.Equal(ErrorCategory.Authentication, attention.Category);
        Assert.Equal(ErrorCategory.None, ok.Category);
    }

    [Fact]
    public async Task Verbose_log_messages_use_the_short_id_and_no_names_or_item_text()
    {
        using var rig = new SyncRig();
        var log = new ListLogger { MinimumLevel = LogLevel.Debug };
        using var engine = new SyncEngine(rig.Store, rig.Store, rig.Provider, rig.Clock, logger: log);
        var collection = new CollectionInfo("MARKER_ID", "MARKER_COLLECTION", "MARKER_COLLECTION_NAME", CollectionKind.TaskList);
        await rig.Store.SaveSourceAsync(new SourceInfo("MARKER_ID", "scripted", "MARKER_NAME"), SourceConfig.Empty, Ct);
        rig.Provider.Sources["MARKER_ID"] = new ScriptedSource("MARKER_ID", collection)
        {
            OnTasks = (_, _) => Task.FromResult(SyncResult.Snapshot<TaskItem>([new TaskItem(new ItemKey("MARKER_ID", "MARKER_COLLECTION", "i"), "MARKER_TITLE")], new SyncState("t"))),
        };

        await engine.SyncSourceAsync("MARKER_ID", Ct);

        Assert.NotEmpty(log.Entries);
        Assert.All(log.Entries, entry => Assert.DoesNotContain("MARKER", entry, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log.Entries, entry => entry.Contains("read as full", StringComparison.Ordinal));
    }
}
