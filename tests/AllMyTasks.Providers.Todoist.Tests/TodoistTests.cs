using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Todoist.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return Respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class TodoistTests
{
    internal const string Secret = "tok_SECRET_123";
    private static readonly string[] Projects = ["p1", "p2", "p3"];
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static string Item(string id, string project = "p1", string content = "Task", string extra = "") =>
        $$"""{"id":"{{id}}","project_id":"{{project}}","content":"{{content}}","priority":1,"labels":[],"checked":false{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static string Reply(string token, bool full, string items = "", string projects = "")
    {
        var key = full ? "true" : "false";
        return $$"""{"sync_token":"{{token}}","full_sync":{{key}},"projects":[{{projects}}],"items":[{{items}}]}""";
    }

    private static string Project(string id, string name = "Inbox", string color = "berry_red", string extra = "") =>
        $$"""{"id":"{{id}}","name":"{{name}}","color":"{{color}}"{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static (FakeHandler Handler, TodoistSource Source) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond, TimeZoneInfo? zone = null)
    {
        var handler = new FakeHandler { Respond = respond };
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("td-1", "token", Secret, Ct).GetAwaiter().GetResult();
        var factory = new TodoistFactory(handler, displayZone: zone ?? Warsaw);
        return (handler, (TodoistSource)factory.Create("td-1", "Todoist", SourceConfig.Empty, secrets));
    }

    private static CollectionInfo Collection(string id = "p1") => new("td-1", id, "Project", CollectionKind.TaskList);

    private static (FakeHandler Handler, TodoistSource Source) CreateWithItem(string item, TimeZoneInfo? zone = null) =>
        Create((_, _) => FakeHandler.Json(Reply("t1", true, item)), zone);

    private static async Task<TaskItem> MapOneAsync(string extra, TimeZoneInfo? zone = null)
    {
        var (_, source) = CreateWithItem(Item("i1", extra: extra), zone);
        var result = await source.SyncTasksAsync(Collection(), null, Ct);
        return Assert.Single(result.Upserts);
    }

    [Fact]
    public async Task Collections_are_the_active_projects_read_only_with_palette_colours()
    {
        var (handler, source) = Create((_, _) => FakeHandler.Json(Reply("t0", true, projects: string.Join(',', Project("p1", "Inbox", extra: "\"inbox_project\":true"), Project("p2", "Work", "mystery_color"), Project("p3", "Old", extra: "\"is_archived\":true"), Project("p4", "Gone", extra: "\"is_deleted\":true")))));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["Inbox", "Work"], collections.Select(c => c.DisplayName));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.Kind == CollectionKind.TaskList && c.SourceId == "td-1"));
        Assert.Equal("#b8255f", collections[0].Color);
        Assert.Null(collections[1].Color);
        Assert.Contains("resource_types=%5B%22projects%22%5D", handler.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_sync_is_a_snapshot_of_the_collection_with_the_new_token_as_state()
    {
        var (handler, source) = Create((_, _) => FakeHandler.Json(Reply("t1", true, string.Join(',', Item("a"), Item("b", "p2")))));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.True(result.ConfirmedEmpty);
        Assert.Equal("a", Assert.Single(result.Upserts).Key.ItemId);
        Assert.Equal("t1", result.NewState.Token);
        var (request, body) = handler.Requests.Single();
        Assert.Contains("sync_token=%2A", body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.todoist.com/api/v1/sync", request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Secret), request.Headers.Authorization);
    }

    [Fact]
    public async Task An_empty_delta_changes_nothing_and_keeps_the_new_token()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json(Reply("t2", false)));

        var result = await source.SyncTasksAsync(Collection(), new SyncState("t1"), Ct);

        Assert.False(result.IsFullSnapshot);
        Assert.Empty(result.Upserts);
        Assert.Empty(result.DeletedItemIds);
        Assert.Equal("t2", result.NewState.Token);
    }

    [Fact]
    public async Task A_delta_upserts_changes_and_deletes_removed_and_moved_items()
    {
        var items = string.Join(',', Item("changed", content: "New title"), Item("removed", extra: "\"is_deleted\":true"), Item("moved-away", "p2"));
        var (handler, source) = Create((_, _) => FakeHandler.Json(Reply("t2", false, items)));

        var result = await source.SyncTasksAsync(Collection(), new SyncState("t1"), Ct);

        Assert.Equal("New title", Assert.Single(result.Upserts).Title);
        Assert.Equal(["removed", "moved-away"], result.DeletedItemIds);
        Assert.Contains("sync_token=t1", handler.Requests.Single().Body, StringComparison.Ordinal);

        // The project the item moved to gets it as an upsert.
        var other = await source.SyncTasksAsync(Collection("p2"), new SyncState("t1"), Ct);
        Assert.Equal("moved-away", Assert.Single(other.Upserts).Key.ItemId);
    }

    [Fact]
    public async Task A_full_sync_reply_to_an_incremental_request_expires_the_state()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json(Reply("t9", true)));

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncTasksAsync(Collection(), new SyncState("old"), Ct));
    }

    [Fact]
    public async Task A_bad_request_to_an_incremental_sync_expires_the_state_but_not_a_first_sync()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json("{}", HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<SyncStateExpiredException>(() => source.SyncTasksAsync(Collection(), new SyncState("old"), Ct));
        var ex = await Assert.ThrowsAsync<SourceException>(() => source.SyncTasksAsync(Collection(), null, Ct));
        Assert.IsNotType<SyncStateExpiredException>(ex);
    }

    [Fact]
    public async Task Collections_synced_from_the_same_token_cause_one_request()
    {
        var items = string.Join(',', Item("a", "p1"), Item("b", "p2"), Item("c", "p3"));
        var (handler, source) = Create((_, _) => FakeHandler.Json(Reply("t1", true, items)));

        var results = await Task.WhenAll(Projects.Select(p => source.SyncTasksAsync(Collection(p), null, Ct)));

        Assert.Single(handler.Requests);
        Assert.Equal(["a", "b", "c"], results.Select(r => Assert.Single(r.Upserts).Key.ItemId));
    }

    [Fact]
    public async Task A_failed_request_is_not_remembered()
    {
        var calls = 0;
        var (handler, source) = Create((_, _) => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FakeHandler.Json(Reply("t1", true, Item("a"))));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));
        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Single(result.Upserts);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_tokens_need_attention(HttpStatusCode status)
    {
        var (_, source) = Create((_, _) => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.DoesNotContain(Secret, ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_requests_is_unavailable_and_reports_retry_after()
    {
        var (_, source) = Create((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.Contains("42", ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_errors_are_unavailable()
    {
        var (_, source) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));
    }

    [Fact]
    public async Task Malformed_json_fails_the_sync_without_quoting_the_body()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json("<html>secret page</html>"));

        var ex = await Assert.ThrowsAsync<SourceDataException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.DoesNotContain("secret page", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_without_a_sync_token_is_refused()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json("""{"items":[]}"""));

        await Assert.ThrowsAsync<SourceDataException>(() => source.SyncTasksAsync(Collection(), null, Ct));
    }

    [Fact]
    public async Task Redirects_are_not_followed_so_the_token_stays_on_the_todoist_host()
    {
        var (handler, source) = Create((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });

        var ex = await Assert.ThrowsAsync<SourceDataException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("evil.example", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_or_unusable_token_is_an_auth_problem_and_nothing_is_sent()
    {
        var handler = new FakeHandler();
        var secrets = new InMemorySecretStore();
        var factory = new TodoistFactory(handler);
        var source = (TodoistSource)factory.Create("td-1", "Todoist", SourceConfig.Empty, secrets);

        await Assert.ThrowsAsync<SourceAuthException>(() => source.SyncTasksAsync(Collection(), null, Ct));
        await secrets.SetAsync("td-1", "token", "bad token\r\nX: y", Ct);
        await Assert.ThrowsAsync<SourceAuthException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_oversized_reply_is_refused()
    {
        var handler = new FakeHandler { Respond = (_, _) => FakeHandler.Json(Reply("t1", true, Item("a"))) };
        using var client = new TodoistClient(handler, _ => Task.FromResult<string?>(Secret), maxBytes: 20);

        await Assert.ThrowsAsync<SourceDataException>(() => client.SyncAsync("*", TodoistClient.ProjectsAndItems, Ct));
    }

    [Fact]
    public async Task One_unreadable_item_is_skipped_with_a_warning()
    {
        var items = string.Join(',', Item("good"), Item("bad", extra: """
            "due":{"date":"not a date"}
            """));
        var (_, source) = Create((_, _) => FakeHandler.Json(Reply("t1", true, items)));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal("good", Assert.Single(result.Upserts).Key.ItemId);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("bad", warning.ItemId);
        Assert.DoesNotContain("not a date", warning.Message, StringComparison.Ordinal);
    }

    // ---- Mapping ----------------------------------------------------------------------------

    [Fact]
    public async Task A_date_only_due_value_is_a_due_date()
    {
        var task = await MapOneAsync("""
            "due":{"date":"2026-11-03","string":"Nov 3","is_recurring":false}
            """);

        Assert.Equal(new DueDate(new DateOnly(2026, 11, 3)), task.Due);
    }

    [Fact]
    public async Task A_utc_due_time_keeps_the_instant_and_the_zone_name()
    {
        var task = await MapOneAsync("""
            "due":{"date":"2026-11-03T14:30:00Z","timezone":"America/New_York","string":"Nov 3 9:30am"}
            """);

        Assert.Equal(new DueDateTime(new DateTimeOffset(2026, 11, 3, 14, 30, 0, TimeSpan.Zero), "America/New_York"), task.Due);
    }

    [Theory]
    [InlineData("2026-11-03T09:00:00", 8)]
    [InlineData("2026-11-03T09:00:00.000000", 8)]
    [InlineData("2026-07-03T09:00:00", 7)]
    public async Task A_floating_due_time_is_read_in_the_display_zone(string date, int expectedUtcHour)
    {
        var task = await MapOneAsync($$"""
            "due":{"date":"{{date}}","timezone":null,"string":"every day at 9"}
            """);

        var due = Assert.IsType<DueDateTime>(task.Due);
        Assert.Equal(expectedUtcHour, due.At.UtcDateTime.Hour);
        Assert.Null(due.TimeZoneId);
    }

    [Theory]
    [InlineData(4, Priority.High)]
    [InlineData(3, Priority.Medium)]
    [InlineData(2, Priority.Low)]
    [InlineData(1, Priority.None)]
    [InlineData(0, Priority.None)]
    public void Api_priority_is_inverted_from_the_app_label(int api, Priority expected) =>
        Assert.Equal(expected, TodoistMapper.MapPriority(api));

    [Fact]
    public async Task Fields_labels_parent_link_and_times_are_mapped()
    {
        var task = await MapOneAsync("""
            "description":"Two pints","priority":4,"labels":["home","errand"],"parent_id":"i0","updated_at":"2026-10-20T08:15:30.123456Z"
            """);

        Assert.Equal("Task", task.Title);
        Assert.Equal("Two pints", task.Notes);
        Assert.Equal(Priority.High, task.Priority);
        Assert.Equal(["home", "errand"], task.Labels);
        Assert.Equal(new ItemKey("td-1", "p1", "i0"), task.ParentKey);
        Assert.Equal(new Uri("https://app.todoist.com/app/task/i1"), task.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 30, TimeSpan.Zero).AddTicks(1234560), task.LastModified);
        Assert.Null(task.Recurrence);
        Assert.Equal(TaskItemStatus.Open, task.Status);
    }

    [Fact]
    public async Task A_checked_item_in_a_delta_is_completed_with_its_time()
    {
        var item = Item("i1").Replace("\"checked\":false", "\"checked\":true,\"completed_at\":\"2026-10-21T10:00:00Z\"", StringComparison.Ordinal);
        var (_, source) = Create((_, _) => FakeHandler.Json(Reply("t2", false, item)));

        var result = await source.SyncTasksAsync(Collection(), new SyncState("t1"), Ct);

        var task = Assert.Single(result.Upserts);
        Assert.Equal(TaskItemStatus.Completed, task.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), task.CompletedAt);
    }

    [Fact]
    public async Task Empty_content_and_description_are_kept_as_is_or_null_and_a_self_parent_is_ignored()
    {
        var task = await MapOneAsync("""
            "description":"","parent_id":"i1"
            """);

        Assert.Null(task.Notes);
        Assert.Null(task.ParentKey);
    }

    [Fact]
    public async Task Numeric_ids_are_accepted()
    {
        var (_, source) = Create((_, _) => FakeHandler.Json("""{"sync_token":"t1","full_sync":true,"items":[{"id":123,"project_id":456,"content":"n"}]}"""));

        var result = await source.SyncTasksAsync(Collection("456"), null, Ct);

        Assert.Equal("123", Assert.Single(result.Upserts).Key.ItemId);
    }

    // ---- Factory and secrets ----------------------------------------------------------------

    [Fact]
    public void Schema_asks_for_a_secret_token_and_an_optional_interval()
    {
        using var factory = new TodoistFactory(new FakeHandler());

        var token = factory.ConfigSchema.Fields.Single(f => f.Key == "token");
        Assert.True(token.Required);
        Assert.True(token.IsSecretField);
        Assert.Equal(FieldKind.Number, factory.ConfigSchema.Fields.Single(f => f.Key == SyncScheduler.IntervalKey).Kind);
        Assert.Equal("todoist", factory.Kind);
    }

    [Fact]
    public async Task Added_through_the_registry_the_token_is_only_in_the_secret_store()
    {
        using var factory = new TodoistFactory(new FakeHandler());
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        var registry = new SourceRegistry([factory], store, secrets);

        var info = await registry.AddAsync("todoist", "Todoist", new Dictionary<string, string> { ["token"] = Secret, ["refreshMinutes"] = "30" }, Ct);

        Assert.Equal(Secret, await secrets.GetAsync(info.Id, "token", Ct));
        var config = (await store.GetSourceConfigAsync(info.Id, Ct))!;
        Assert.Equal(["refreshMinutes"], config.Values.Keys);
    }

    [Fact]
    public async Task Through_the_engine_a_sync_fills_the_cache_and_the_token_never_appears_in_text()
    {
        var handler = new FakeHandler { Respond = (_, _) => FakeHandler.Json(Reply("t1", true, Item("a"), Project("p1", "Inbox"))) };
        using var factory = new TodoistFactory(handler, displayZone: TimeZoneInfo.Utc);
        var store = new InMemoryStore();
        var secrets = new InMemorySecretStore();
        var registry = new SourceRegistry([factory], store, secrets);
        var info = await registry.AddAsync("todoist", "Todoist", new Dictionary<string, string> { ["token"] = Secret }, Ct);
        using var engine = new SyncEngine(store, store, registry, TimeProvider.System);

        await engine.SyncSourceAsync(info.Id, Ct);

        var status = (await engine.GetStatusesAsync(Ct)).Single();
        Assert.Equal(SourceStatusKind.Ok, status.Kind);
        Assert.DoesNotContain(Secret, status.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, (await registry.GetAsync(info, Ct)).ToString(), StringComparison.Ordinal);
    }
}
