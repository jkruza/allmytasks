using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;

namespace AllMyTasks.Storage.Sqlite.Tests;

public class SqliteStoreTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static ItemKey Key(string id, string source = "s", string collection = "c") => new(source, collection, id);

    private static CalendarEvent Timed(string id, DateTimeOffset start, int minutes = 30, Recurrence? recurrence = null) =>
        new(Key(id), id, new TimedEvent(start, start.AddMinutes(minutes), "UTC"), Recurrence: recurrence);

    private static SyncResult<T> Snapshot<T>(params T[] items) => SyncResult.Snapshot<T>(items, new SyncState("t"));

    private static async Task<IReadOnlyList<CalendarEvent>> Range(SqliteItemStore store, DateTimeOffset from, DateTimeOffset to) =>
        await store.GetEventCandidatesAsync(from, to, Utc, Ct);

    [Fact]
    public async Task Events_round_trip_with_every_field()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var rec = new Recurrence("FREQ=WEEKLY;UNTIL=20261231T000000Z", [T0.AddDays(2)], [T0.AddDays(7)]);
        var full = new CalendarEvent(
            Key("full"),
            "Title with ünïcode ✓",
            new TimedEvent(T0, T0.AddHours(1), "Europe/Warsaw"),
            Description: "desc",
            Location: "loc",
            Url: new Uri("https://example.com/x"),
            Status: EventStatus.Tentative,
            Availability: Availability.Free,
            Recurrence: rec,
            Categories: ["a", "b"],
            LastModified: T0.AddDays(-1),
            MeetingUrl: new Uri("https://meet.google.com/abc-defg-hij"),
            Organizer: new Attendee("Olga", "o@example.com", IsSelf: false),
            Attendees: [new Attendee("Ann", "ann@example.com", AttendeeRole.Optional, ResponseStatus.Tentative, IsSelf: true), new Attendee(null, "room@example.com", AttendeeRole.Resource, ResponseStatus.Accepted)],
            AttendeeCount: 340);
        var allDay = new CalendarEvent(Key("allday"), "Holiday", new AllDayEvent(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 27)));
        var floating = new CalendarEvent(Key("float"), "Pill", new TimedEvent(T0, T0.AddMinutes(5), null, IsFloating: true));
        var over = new CalendarEvent(Key("full") with { RecurrenceId = T0.AddDays(7) }, "Moved", new TimedEvent(T0.AddDays(7).AddHours(2), T0.AddDays(7).AddHours(3)));
        await store.ApplyAsync("s", "c", Snapshot(full, allDay, floating, over), Ct);

        var loaded = (await Range(store, T0.AddDays(-400), T0.AddDays(400))).ToDictionary(e => e.Key);

        Assert.Equal(4, loaded.Count);
        var f = loaded[full.Key];
        Assert.Equal(full.Title, f.Title);
        Assert.Equal(full.Time, f.Time);
        Assert.Equal(("desc", "loc", EventStatus.Tentative, Availability.Free), (f.Description, f.Location, f.Status, f.Availability));
        Assert.Equal(full.Url, f.Url);
        Assert.Equal(["a", "b"], f.Categories);
        Assert.Equal(full.LastModified, f.LastModified);
        Assert.Equal(full.MeetingUrl, f.MeetingUrl);
        Assert.Equal(full.Organizer, f.Organizer);
        Assert.Equal(full.Attendees, f.Attendees);
        Assert.Equal(340, f.AttendeeCount);
        Assert.Null(loaded[allDay.Key].MeetingUrl);
        Assert.Empty(loaded[allDay.Key].Attendees);
        Assert.Equal(("FREQ=WEEKLY;UNTIL=20261231T000000Z", 1, 1), (f.Recurrence!.Rule, f.Recurrence.ExtraDates.Count, f.Recurrence.ExcludedDates.Count));
        Assert.Equal(allDay.Time, loaded[allDay.Key].Time);
        Assert.True(((TimedEvent)loaded[floating.Key].Time).IsFloating);
        Assert.Equal(over.Key, loaded[over.Key].Key);
        Assert.Equal(T0.AddDays(7), loaded[over.Key].Key.RecurrenceId);
    }

    [Fact]
    public async Task Tasks_round_trip_with_every_field()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var parent = new TaskItem(Key("p"), "Parent", Due: new DueDate(new DateOnly(2026, 10, 20)));
        var child = new TaskItem(
            Key("c1"),
            "Child",
            Notes: "n",
            Due: new DueDateTime(T0, "Europe/Warsaw"),
            Start: T0.AddDays(-1),
            Status: TaskItemStatus.InProgress,
            CompletedAt: null,
            Priority: Priority.High,
            Labels: ["x", "y"],
            ParentKey: parent.Key,
            Recurrence: new Recurrence("FREQ=DAILY"),
            Url: new Uri("https://example.com/t"),
            LastModified: T0);
        await store.ApplyAsync("s", "c", Snapshot(parent, child), Ct);

        var loaded = (await store.GetTasksAsync(Ct)).ToDictionary(t => t.Key);

        Assert.Equal(parent.Due, loaded[parent.Key].Due);
        var c = loaded[child.Key];
        Assert.Equal(child.Due, c.Due);
        Assert.Equal((TaskItemStatus.InProgress, Priority.High, parent.Key, "n"), (c.Status, c.Priority, c.ParentKey, c.Notes));
        Assert.Equal(["x", "y"], c.Labels);
        Assert.Equal("FREQ=DAILY", c.Recurrence!.Rule);
        Assert.Equal(child.Url, c.Url);
        Assert.Equal(child.Start, c.Start);
    }

    [Fact]
    public async Task Full_snapshot_replaces_the_collection_and_leaves_others_alone()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        await store.ApplyAsync("s", "c", Snapshot(new TaskItem(Key("1"), "old"), new TaskItem(Key("2"), "gone")), Ct);
        await store.ApplyAsync("s", "other", Snapshot(new TaskItem(Key("3", collection: "other"), "keep")), Ct);

        await store.ApplyAsync("s", "c", Snapshot(new TaskItem(Key("1"), "new")), Ct);

        Assert.Equal(["keep", "new"], (await store.GetTasksAsync(Ct)).Select(t => t.Title).Order());
    }

    [Fact]
    public async Task Delta_upserts_and_deletes_by_item_id_including_overrides()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var master = Timed("series", T0, recurrence: new Recurrence("FREQ=DAILY"));
        var over = new CalendarEvent(master.Key with { RecurrenceId = T0.AddDays(1) }, "o", new TimedEvent(T0.AddDays(1), T0.AddDays(1).AddHours(1)));
        await store.ApplyAsync("s", "c", Snapshot(master, over, Timed("single", T0)), Ct);

        await store.ApplyAsync("s", "c", SyncResult.Delta([Timed("single", T0, 60)], ["series"], new SyncState("t2")), Ct);

        var left = await Range(store, T0.AddDays(-1), T0.AddDays(5));
        Assert.Equal("single", Assert.Single(left).Title);
        Assert.Equal(TimeSpan.FromMinutes(60), ((TimedEvent)left[0].Time).End - ((TimedEvent)left[0].Time).Start);
    }

    [Fact]
    public async Task Range_query_returns_overlapping_singles_only()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        await store.ApplyAsync("s", "c", Snapshot(
            Timed("in", T0),
            Timed("before", T0.AddDays(-30)),
            Timed("after", T0.AddDays(30)),
            Timed("spans", T0.AddDays(-2), 60 * 24 * 5)), Ct);

        var found = await Range(store, T0.AddHours(-1), T0.AddHours(2));

        Assert.Equal(["in", "spans"], found.Select(e => e.Title).Order());
    }

    [Fact]
    public async Task Recurring_series_are_candidates_until_their_end_and_bring_their_overrides()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var open = Timed("open-ended", T0.AddDays(-100), recurrence: new Recurrence("FREQ=WEEKLY"));
        var counted = Timed("counted", T0.AddDays(-100), recurrence: new Recurrence("FREQ=WEEKLY;COUNT=5"));
        var finished = Timed("finished", T0.AddDays(-100), recurrence: new Recurrence("FREQ=WEEKLY;UNTIL=20260101T000000Z"));
        var rdateOnly = Timed("rdates", T0.AddDays(-100), recurrence: new Recurrence(string.Empty, [T0.AddDays(-90)], []));
        var override1 = new CalendarEvent(open.Key with { RecurrenceId = T0.AddDays(-93) }, "ovr", new TimedEvent(T0.AddDays(-93), T0.AddDays(-93).AddHours(1)));
        var future = Timed("future", T0.AddDays(100), recurrence: new Recurrence("FREQ=WEEKLY"));
        await store.ApplyAsync("s", "c", Snapshot(open, counted, finished, rdateOnly, override1, future), Ct);

        var found = await Range(store, T0, T0.AddDays(1));

        Assert.Equal(["counted", "open-ended", "ovr"], found.Select(e => e.Title).Order());
    }

    [Fact]
    public async Task Date_only_until_covers_that_whole_day()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), RecurrenceBounds.ParseUntil("FREQ=DAILY;UNTIL=20260101"));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero), RecurrenceBounds.ParseUntil("FREQ=DAILY;UNTIL=20260101T123000Z"));
        Assert.Null(RecurrenceBounds.ParseUntil("FREQ=DAILY;COUNT=3"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task All_day_events_near_the_range_edge_are_returned_whatever_the_zone()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var xmas = new CalendarEvent(Key("xmas"), "xmas", new AllDayEvent(new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26)));
        await store.ApplyAsync("s", "c", Snapshot(xmas), Ct);
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        // In Tokyo, 25 December begins at 24 December 15:00 UTC.
        var from = new DateTimeOffset(2026, 12, 24, 15, 0, 0, TimeSpan.Zero);
        var found = await store.GetEventCandidatesAsync(from, from.AddDays(1), tokyo, Ct);

        Assert.Single(found);
    }

    [Fact]
    public async Task Sources_collections_preferences_and_sync_state_persist_across_reopen()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        var source = new SourceInfo("s", "ical", "My feed");
        await repo.SaveSourceAsync(source, new SourceConfig(new Dictionary<string, string> { ["url"] = "https://example.com" }), Ct);
        await repo.SaveCollectionsAsync(source, [new CollectionInfo("s", "c", "Cal", CollectionKind.Calendar, "#111111", IsReadOnly: true)], Ct);
        await repo.SetCollectionPreferencesAsync("s", "c", false, "#abcdef", Ct);
        await repo.SaveSyncStateAsync("s", "c", new SyncState("tok"), Ct);

        var reopened = new SqliteSourceRepository(await SqliteDatabase.OpenAsync(temp.FilePath, Ct));

        Assert.Equal(["My feed"], (await reopened.GetSourcesAsync(Ct)).Select(s => s.DisplayName));
        Assert.Equal("https://example.com", (await reopened.GetSourceConfigAsync("s", Ct))!.Get("url"));
        var c = Assert.Single(await reopened.GetCollectionsAsync(Ct));
        Assert.Equal((false, "#abcdef", true), (c.IsVisible, c.Color, c.IsReadOnly));
        Assert.Equal("tok", (await reopened.GetSyncStateAsync("s", "c", Ct))!.Token);
    }

    [Fact]
    public async Task The_last_error_detail_persists_and_is_cleared_with_the_cache()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        await repo.SaveSourceAsync(new SourceInfo("s", "ical", "My feed"), new SourceConfig(new Dictionary<string, string>()), Ct);
        await repo.SaveSourceStatusAsync(new SourceStatus("s", SourceStatusKind.Error, LastError: "The sync failed.", LastErrorDetail: "HTTP 404"), Ct);

        var reopened = new SqliteSourceRepository(await SqliteDatabase.OpenAsync(temp.FilePath, Ct));

        var status = Assert.Single(await reopened.GetSourceStatusesAsync(Ct));
        Assert.Equal(("The sync failed.", "HTTP 404"), (status.LastError, status.LastErrorDetail));
        await reopened.ResetCacheAsync(Ct);
        Assert.Null(Assert.Single(await reopened.GetSourceStatusesAsync(Ct)).LastErrorDetail);
    }

    [Fact]
    public async Task Provider_refresh_keeps_user_choices_and_drops_vanished_collections_with_their_items()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        var store = new SqliteItemStore(db);
        var source = new SourceInfo("s", "caldav", "Server");
        await repo.SaveCollectionsAsync(source,
        [
            new CollectionInfo("s", "a", "A", CollectionKind.TaskList, "#111111"),
            new CollectionInfo("s", "b", "B", CollectionKind.TaskList),
        ], Ct);
        await repo.SetCollectionPreferencesAsync("s", "a", false, null, Ct);
        await store.ApplyAsync("s", "b", Snapshot(new TaskItem(Key("1", collection: "b"), "in b")), Ct);

        await repo.SaveCollectionsAsync(source, [new CollectionInfo("s", "a", "A renamed", CollectionKind.TaskList, "#222222")], Ct);

        var a = Assert.Single(await repo.GetCollectionsAsync(Ct));
        Assert.Equal(("A renamed", "#222222", false), (a.DisplayName, a.Color, a.IsVisible));
        Assert.Empty(await store.GetTasksAsync(Ct));
    }

    [Fact]
    public async Task Removing_a_source_removes_everything_of_it_together()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        var store = new SqliteItemStore(db);
        var one = new SourceInfo("one", "ical", "One");
        var two = new SourceInfo("two", "ical", "Two");
        foreach (var s in new[] { one, two })
        {
            await repo.SaveSourceAsync(s, SourceConfig.Empty, Ct);
            await repo.SaveCollectionsAsync(s, [new CollectionInfo(s.Id, "c", "C", CollectionKind.Calendar)], Ct);
            await store.ApplyAsync(s.Id, "c", Snapshot(new CalendarEvent(Key("e", s.Id), "e", new TimedEvent(T0, T0.AddHours(1)))), Ct);
            await store.ApplyAsync(s.Id, "c", Snapshot(new TaskItem(Key("t", s.Id), "t")), Ct);
        }

        await repo.RemoveSourceAsync("one", Ct);

        Assert.Equal(["two"], (await repo.GetSourcesAsync(Ct)).Select(s => s.Id));
        Assert.Equal(["two"], (await repo.GetCollectionsAsync(Ct)).Select(c => c.SourceId));
        Assert.Equal(["two"], (await store.GetTasksAsync(Ct)).Select(t => t.Key.SourceId));
        Assert.Equal(["two"], (await Range(store, T0.AddDays(-1), T0.AddDays(1))).Select(e => e.Key.SourceId));
    }

    [Fact]
    public async Task Reset_cache_drops_items_and_sync_state_but_keeps_sources_and_preferences()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        var store = new SqliteItemStore(db);
        var s = new SourceInfo("s", "ical", "S");
        await repo.SaveSourceAsync(s, SourceConfig.Empty, Ct);
        await repo.SaveCollectionsAsync(s, [new CollectionInfo("s", "c", "C", CollectionKind.TaskList)], Ct);
        await repo.SetCollectionPreferencesAsync("s", "c", false, null, Ct);
        await repo.SaveSyncStateAsync("s", "c", new SyncState("x"), Ct);
        await store.ApplyAsync("s", "c", Snapshot(new TaskItem(Key("1"), "t")), Ct);

        await repo.ResetCacheAsync(Ct);

        Assert.Empty(await store.GetTasksAsync(Ct));
        Assert.Null(await repo.GetSyncStateAsync("s", "c", Ct));
        Assert.Single(await repo.GetSourcesAsync(Ct));
        Assert.False(Assert.Single(await repo.GetCollectionsAsync(Ct)).IsVisible);
    }

    [Fact]
    public async Task Settings_store_gets_sets_overwrites_and_removes()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var settings = new SqliteSettingsStore(db);

        Assert.Null(await settings.GetAsync("mode", Ct));
        await settings.SetAsync("mode", "week", Ct);
        await settings.SetAsync("mode", "month", Ct);
        Assert.Equal("month", await settings.GetAsync("mode", Ct));
        await settings.RemoveAsync("mode", Ct);
        Assert.Null(await settings.GetAsync("mode", Ct));
    }

    [Fact]
    public async Task Migrations_are_idempotent_and_record_the_version()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;

        var again = await SqliteDatabase.OpenAsync(temp.FilePath, Ct);

        Assert.Equal(SqliteDatabase.LatestVersion, await again.GetVersionAsync(Ct));
        Assert.True(SqliteDatabase.LatestVersion >= 1);
        Assert.Equal(SqliteDatabase.LatestVersion, await db.GetVersionAsync(Ct));
    }

    [Fact]
    public async Task Rows_cached_before_meeting_data_read_as_having_none()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        var ev = new CalendarEvent(Key("old"), "Old", new TimedEvent(T0, T0.AddHours(1), "UTC"));
        await store.ApplyAsync("s", "c", Snapshot(ev), Ct);
        await using (var connection = await db.OpenConnectionAsync(Ct))
        {
            // The JSON an earlier version wrote has none of the new members.
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET json = json_remove(json, '$.MeetingUrl', '$.Organizer', '$.Attendees', '$.AttendeeCount');";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var loaded = Assert.Single(await Range(store, T0.AddDays(-1), T0.AddDays(1)));

        Assert.Null(loaded.MeetingUrl);
        Assert.Null(loaded.Organizer);
        Assert.Empty(loaded.Attendees);
        Assert.Equal(0, loaded.AttendeeCount);
    }

    [Fact]
    public async Task The_meeting_migration_clears_calendar_sync_state_once_and_keeps_task_state()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        await Exec(db, "UPDATE schema_info SET version = 3;");
        await Exec(db, "INSERT INTO sources (id, kind, display_name) VALUES ('s', 'google', 'G');");
        await Exec(db, "INSERT INTO collections (source_id, id, display_name, kind, sync_token) VALUES ('s', 'cal', 'Cal', 0, 'tok1'), ('s', 'tasks', 'Tasks', 1, 'tok2');");

        var migrated = await SqliteDatabase.OpenAsync(temp.FilePath, Ct);
        var repo = new SqliteSourceRepository(migrated);

        Assert.Null(await repo.GetSyncStateAsync("s", "cal", Ct));
        Assert.Equal("tok2", (await repo.GetSyncStateAsync("s", "tasks", Ct))?.Token);

        // A later sync saves a token again; opening the database again must not clear it.
        await repo.SaveSyncStateAsync("s", "cal", new SyncState("tok3"), Ct);
        await SqliteDatabase.OpenAsync(temp.FilePath, Ct);
        Assert.Equal("tok3", (await repo.GetSyncStateAsync("s", "cal", Ct))?.Token);
    }

    private static async Task Exec(SqliteDatabase db, string sql)
    {
        await using var connection = await db.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task A_database_from_a_newer_version_is_a_hard_error_and_is_left_untouched()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        await using (var connection = await db.OpenConnectionAsync(Ct))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE schema_info SET version = {SqliteDatabase.LatestVersion + 5};";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var ex = await Assert.ThrowsAsync<StorageVersionException>(() => SqliteDatabase.OpenAsync(temp.FilePath, Ct));

        Assert.Equal(SqliteDatabase.LatestVersion + 5, ex.DatabaseVersion);
        Assert.Equal(SqliteDatabase.LatestVersion + 5, await db.GetVersionAsync(Ct));
    }

    [Fact]
    public async Task Sample_data_seeded_through_sync_gives_the_same_views_from_sqlite_as_from_memory_and_survives_restart()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var today = new DateOnly(2026, 10, 20);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(2)));
        var memory = await SampleData.CreateStoreAsync(today, zone, Ct);
        var items = new SqliteItemStore(db);
        var repo = new SqliteSourceRepository(db);
        await SampleData.SeedAsync(items, repo, today, zone, Ct);

        var expected = await Entries(new AgendaService(memory, memory, new FakeRecurrenceExpander(), clock), today, zone);
        var actual = await Entries(new AgendaService(items, repo, new FakeRecurrenceExpander(), clock), today, zone);
        var db2 = await SqliteDatabase.OpenAsync(temp.FilePath, Ct);
        var restarted = await Entries(new AgendaService(new SqliteItemStore(db2), new SqliteSourceRepository(db2), new FakeRecurrenceExpander(), clock), today, zone);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        Assert.Equal(expected, restarted);
    }

    private static async Task<List<string>> Entries(AgendaService service, DateOnly today, TimeZoneInfo zone)
    {
        var entries = await service.GetEntriesAsync(new ViewQuery(today.AddDays(-14), today.AddDays(28), zone, new ViewFilter(ShowCompleted: true), IncludeOverdue: true), Ct);
        return [.. entries.Select(e => $"{e.Key.SourceId}/{e.Key.ItemId}|{e.Title}|{e.Start:O}|{e.End:O}|{e.CollectionName}|{e.Color}")];
    }

    [Fact]
    public async Task A_failed_apply_leaves_the_cache_untouched()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var store = new SqliteItemStore(db);
        await store.ApplyAsync("s", "c", Snapshot(new TaskItem(Key("1"), "keep")), Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ApplyAsync("s", "c", Snapshot(new TaskItem(Key("2"), "new")), cts.Token));

        Assert.Equal(["keep"], (await store.GetTasksAsync(Ct)).Select(t => t.Title));
    }

    [Fact]
    public async Task Pending_changes_round_trip_in_order_and_survive_reopening_the_file()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        await new SqliteSourceRepository(db).SaveSourceAsync(new SourceInfo("s", "k", "S"), SourceConfig.Empty, Ct);
        var store = new SqlitePendingChangeStore(db);
        var later = new PendingChange(Guid.NewGuid(), Key("b"), PendingKind.Reopen, T0.AddSeconds(5));
        var first = new PendingChange(Guid.NewGuid(), Key("a"), PendingKind.Complete, T0, 2, PendingState.Failed, "refused");
        await store.SaveAsync(later, Ct);
        await store.SaveAsync(first, Ct);

        var again = new SqlitePendingChangeStore(await SqliteDatabase.OpenAsync(db.Path, Ct));
        var loaded = await again.GetAllAsync(Ct);

        Assert.Equal([first, later], loaded);
    }

    [Fact]
    public async Task Saving_a_change_again_updates_it_and_removing_is_idempotent()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        await new SqliteSourceRepository(db).SaveSourceAsync(new SourceInfo("s", "k", "S"), SourceConfig.Empty, Ct);
        var store = new SqlitePendingChangeStore(db);
        var change = new PendingChange(Guid.NewGuid(), Key("a"), PendingKind.Complete, T0);
        await store.SaveAsync(change, Ct);

        await store.SaveAsync(change with { Attempts = 3, State = PendingState.Failed, Error = "no" }, Ct);
        var updated = Assert.Single(await store.GetAllAsync(Ct));
        await store.RemoveAsync(change.Id, Ct);
        await store.RemoveAsync(change.Id, Ct);

        Assert.Equal((3, PendingState.Failed, "no"), (updated.Attempts, updated.State, updated.Error));
        Assert.Empty(await store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task Removing_a_source_removes_its_pending_changes_and_keeps_those_of_others()
    {
        var (temp, db) = await TempDatabase.CreateAsync();
        using var _ = temp;
        var repo = new SqliteSourceRepository(db);
        await repo.SaveSourceAsync(new SourceInfo("one", "k", "One"), SourceConfig.Empty, Ct);
        await repo.SaveSourceAsync(new SourceInfo("two", "k", "Two"), SourceConfig.Empty, Ct);
        var store = new SqlitePendingChangeStore(db);
        await store.SaveAsync(new PendingChange(Guid.NewGuid(), Key("a", "one"), PendingKind.Complete, T0), Ct);
        await store.SaveAsync(new PendingChange(Guid.NewGuid(), Key("b", "two"), PendingKind.Complete, T0), Ct);

        await repo.RemoveSourceAsync("one", Ct);

        Assert.Equal(["two"], (await store.GetAllAsync(Ct)).Select(c => c.Key.SourceId));
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}
