using System.Net;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Google.Tests;

public class TaskTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string TaskJson(string id, string extra = "", string updated = "2026-10-26T10:00:00.000Z") =>
        $$"""{"id":"{{id}}","title":"Task {{id}}","status":"needsAction","updated":"{{updated}}"{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static async System.Threading.Tasks.Task<(TaskItem Task, SyncResult<TaskItem> Result)> MapOneAsync(string extra)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Tasks(TaskJson("t1", extra)), calendar: false);
        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);
        return (result.Upserts[0], result);
    }

    private static SyncState State(string updatedMin = "2026-10-26T10:00:00Z", string fullAt = "2026-10-25T00:00:00Z") =>
        new($$"""{"v":1,"updatedMin":"{{updatedMin}}","fullAt":"{{fullAt}}"}""");

    [Fact]
    public async Task Lists_are_read_only_task_lists_shown_as_returned_with_paging()
    {
        var (handler, source, _, _) = Setup.Create(
            r => Setup.Query(r, "pageToken") == "p2" ? Setup.Page("""{"id":"b","title":"Shopping"}""") : Setup.Page("""{"id":"a","title":"My Tasks"}""", next: "p2"),
            calendar: false);

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["My Tasks", "Shopping"], collections.Select(c => c.DisplayName));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.IsVisible && c.Kind == CollectionKind.TaskList && c.Color is null));
        Assert.Equal(2, handler.ApiRequests.Count());
        Assert.All(handler.ApiRequests, r => Assert.Equal("tasks.googleapis.com", r.RequestUri!.Host));
    }

    [Fact]
    public async Task A_source_with_both_services_lists_calendars_then_task_lists()
    {
        var (_, source, _, _) = Setup.Create(r => r.RequestUri!.Host == "tasks.googleapis.com"
            ? Setup.Page("""{"id":"l","title":"Tasks"}""")
            : Setup.Page("""{"id":"c","summary":"Cal","selected":true}"""));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal([CollectionKind.Calendar, CollectionKind.TaskList], collections.Select(c => c.Kind));
    }

    [Fact]
    public async Task A_task_maps_its_fields()
    {
        var (t, _) = await MapOneAsync("""
            "notes":"a\nb","due":"2026-10-30T00:00:00.000Z","parent":"p1","webViewLink":"https://tasks.google.com/task/t1","hidden":true
            """);

        Assert.Equal(new ItemKey(Setup.SourceId, "list1", "t1"), t.Key);
        Assert.Equal("Task t1", t.Title);
        Assert.Equal("a\nb", t.Notes);
        Assert.Equal(new DueDate(new DateOnly(2026, 10, 30)), t.Due);
        Assert.Equal(new ItemKey(Setup.SourceId, "list1", "p1"), t.ParentKey);
        Assert.Equal("https://tasks.google.com/task/t1", t.Url?.AbsoluteUri);
        Assert.Equal(TaskItemStatus.Open, t.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), t.LastModified);
        Assert.Equal(Priority.None, t.Priority);
        Assert.Null(t.Recurrence);
    }

    [Fact]
    public async Task A_due_date_is_the_utc_calendar_date_and_never_a_moment_in_a_zone()
    {
        var (t, _) = await MapOneAsync("\"due\":\"2026-10-30T00:00:00.000Z\"");

        Assert.Equal(new DueDate(new DateOnly(2026, 10, 30)), t.Due);
        Assert.IsType<DueDate>(t.Due);
    }

    [Fact]
    public async Task A_completed_task_has_its_completion_time_and_an_empty_title_stays_empty()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Tasks("""{"id":"t1","status":"completed","completed":"2026-10-25T09:30:00.000Z","updated":"2026-10-25T09:30:00.000Z"}"""), calendar: false);

        var t = (await source.SyncTasksAsync(Setup.List(), null, Ct)).Upserts[0];

        Assert.Equal(TaskItemStatus.Completed, t.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero), t.CompletedAt);
        Assert.Equal(string.Empty, t.Title);
    }

    [Fact]
    public async Task Odd_data_adds_warnings_but_keeps_the_task()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Tasks($"""
                {TaskJson("u", "\"status\":\"pending\"").Replace("\"status\":\"needsAction\",", string.Empty, StringComparison.Ordinal)},
                {TaskJson("c", "\"completed\":\"2026-10-25T09:30:00.000Z\"")},
                {TaskJson("s", "\"parent\":\"s\"")},
                {TaskJson("d", "\"due\":\"garbage\"")}
                """),
            calendar: false);

        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);

        Assert.Equal(["u", "c", "s"], result.Upserts.Select(t => t.Key.ItemId));
        Assert.All(result.Upserts, t => Assert.Equal(TaskItemStatus.Open, t.Status));
        Assert.Null(result.Upserts[1].CompletedAt);
        Assert.Null(result.Upserts[2].ParentKey);
        Assert.Equal(["u", "c", "s", "d"], result.Warnings.Select(w => w.ItemId));
    }

    [Fact]
    public async Task First_sync_is_a_snapshot_asking_for_completed_and_hidden_tasks_but_not_deleted_ones()
    {
        var (handler, source, _, time) = Setup.Create(r => Setup.Query(r, "pageToken") == "p2" ? Setup.Tasks(TaskJson("b", updated: "2026-10-26T11:00:00.000Z")) : Setup.Tasks(TaskJson("a"), next: "p2"), calendar: false);

        var result = await source.SyncTasksAsync(Setup.List("L/1"), null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["a", "b"], result.Upserts.Select(t => t.Key.ItemId));
        var calls = handler.ApiRequests.ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c =>
        {
            Assert.Equal("/tasks/v1/lists/L%2F1/tasks", c.RequestUri!.AbsolutePath);
            Assert.Equal("true", Setup.Query(c, "showCompleted"));
            Assert.Equal("true", Setup.Query(c, "showHidden"));
            Assert.Equal(string.Empty, Setup.Query(c, "showDeleted"));
            Assert.Equal(string.Empty, Setup.Query(c, "updatedMin"));
        });
        Assert.Contains("2026-10-26T11:00:00", result.NewState.Token, StringComparison.Ordinal);
        Assert.Contains("2026-10-26T12:00:00", result.NewState.Token, StringComparison.Ordinal);
        Assert.Equal(time.GetUtcNow(), Setup.Now);
    }

    [Fact]
    public async Task An_empty_list_is_confirmed_empty_only_for_a_well_formed_reply()
    {
        var (_, good, _, _) = Setup.Create(_ => Setup.Tasks(string.Empty), calendar: false);
        Assert.True((await good.SyncTasksAsync(Setup.List(), null, Ct)).ConfirmedEmpty);

        var (_, odd, _, _) = Setup.Create(_ => FakeHandler.Json("{}"), calendar: false);
        Assert.False((await odd.SyncTasksAsync(Setup.List(), null, Ct)).ConfirmedEmpty);

        var (_, html, _, _) = Setup.Create(_ => FakeHandler.Json("<html></html>"), calendar: false);
        await Assert.ThrowsAsync<SourceDataException>(() => html.SyncTasksAsync(Setup.List(), null, Ct));
    }

    [Fact]
    public async Task A_delta_asks_for_changes_since_the_cursor_minus_the_overlap_and_reports_tombstones()
    {
        var (handler, source, _, _) = Setup.Create(
            _ => Setup.Tasks($"{TaskJson("a", updated: "2026-10-26T11:30:00.000Z")},{TaskJson("a", "\"title\":\"newer\"", "2026-10-26T11:40:00.000Z")},{{\"id\":\"gone\",\"deleted\":true,\"updated\":\"2026-10-26T11:35:00.000Z\"}}"),
            calendar: false);

        var result = await source.SyncTasksAsync(Setup.List(), State(), Ct);

        Assert.False(result.IsFullSnapshot);
        Assert.Equal("newer", Assert.Single(result.Upserts).Title);
        Assert.Equal(["gone"], result.DeletedItemIds);
        var call = Assert.Single(handler.ApiRequests);
        Assert.Equal("2026-10-26T09:58:00.000Z", Setup.Query(call, "updatedMin"));
        Assert.Equal("true", Setup.Query(call, "showDeleted"));
        Assert.Contains("2026-10-26T11:40:00", result.NewState.Token, StringComparison.Ordinal);
        Assert.Contains("2026-10-25T00:00:00", result.NewState.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_delta_keeps_the_cursor()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Tasks(string.Empty), calendar: false);

        var result = await source.SyncTasksAsync(Setup.List(), State(), Ct);

        Assert.Empty(result.Upserts);
        Assert.Contains("2026-10-26T10:00:00", result.NewState.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_is_taken_again_after_a_week()
    {
        var (handler, source, _, _) = Setup.Create(_ => Setup.Tasks(TaskJson("a")), calendar: false);

        var result = await source.SyncTasksAsync(Setup.List(), State(fullAt: "2026-10-18T11:59:00Z"), Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(string.Empty, Setup.Query(handler.ApiRequests.Single(), "updatedMin"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":9,"updatedMin":"2026-10-26T10:00:00Z","fullAt":"2026-10-25T00:00:00Z"}""")]
    public async Task A_state_that_cannot_be_used_expires(string token)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Tasks(string.Empty), calendar: false);

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncTasksAsync(Setup.List(), new SyncState(token), Ct));
    }

    [Fact]
    public async Task A_deleted_list_keeps_the_cache_and_warns()
    {
        var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound), calendar: false);
        var previous = State();

        var result = await source.SyncTasksAsync(Setup.List(), previous, Ct);

        Assert.Empty(result.Upserts);
        Assert.False(result.IsFullSnapshot);
        Assert.Same(previous, result.NewState);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task A_429_with_a_short_retry_after_is_waited_out_inside_the_client()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Respond = (_, _) => ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(3)) } }
                : Setup.Tasks(string.Empty),
        };
        TimeSpan? waited = null;
        using var client = new GoogleClient(handler, _ => Task.FromResult("t"), () => { }, delay: (wait, _) =>
        {
            waited = wait;
            return Task.CompletedTask;
        });

        var page = await client.GetAsync(new Uri("https://tasks.googleapis.com/tasks/v1/lists/x/tasks"), GoogleJson.Default.ListPageTaskDto, Ct);

        Assert.Equal("tasks#tasks", page.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), waited);
    }
}
