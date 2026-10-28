using System.Net;
using System.Text.Json;
using System.Web;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Providers.Todoist.Tests;

public class TodoistWriteTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly ItemKey Task42 = new("td-1", "p1", "42");

    private static string Status(Guid id, string status) =>
        $$$"""{"sync_token":"t2","full_sync":false,"projects":[],"sync_status":{"{{{id:D}}}":{{{status}}}}}""";

    private static (FakeHandler Handler, ISource Source, TodoistFactory Factory) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond, bool allow = true)
    {
        var handler = new FakeHandler { Respond = respond };
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("td-1", "token", TodoistTests.Secret, Ct).GetAwaiter().GetResult();
        var factory = new TodoistFactory(handler, displayZone: TimeZoneInfo.Utc);
        var config = new SourceConfig(allow ? new Dictionary<string, string> { [TodoistFactory.AllowChangesField] = "true" } : new Dictionary<string, string>());
        return (handler, factory.Create("td-1", "Todoist", config, secrets), factory);
    }

    private static JsonElement Commands(FakeHandler handler, int request = 0) =>
        JsonDocument.Parse(HttpUtility.ParseQueryString(handler.Requests[request].Body)["commands"]!).RootElement;

    [Fact]
    public void Writing_is_only_offered_when_the_source_allows_changes()
    {
        var (_, off, f1) = Create((_, _) => FakeHandler.Json("{}"), allow: false);
        var (_, on, f2) = Create((_, _) => FakeHandler.Json("{}"));
        using var _1 = f1;
        using var _2 = f2;

        Assert.IsNotType<IWritableTaskSource>(off, exactMatch: false);
        Assert.IsAssignableFrom<IWritableTaskSource>(on);
        Assert.IsAssignableFrom<ITaskSource>(off);
    }

    [Fact]
    public async Task Lists_are_read_only_until_changes_are_allowed()
    {
        static HttpResponseMessage Projects() => FakeHandler.Json("""{"sync_token":"t","projects":[{"id":"p1","name":"Inbox"}]}""");
        var (_, off, f1) = Create((_, _) => Projects(), allow: false);
        var (_, on, f2) = Create((_, _) => Projects());
        using var _1 = f1;
        using var _2 = f2;

        Assert.True((await off.GetCollectionsAsync(Ct)).Single().IsReadOnly);
        Assert.False((await on.GetCollectionsAsync(Ct)).Single().IsReadOnly);
    }

    [Fact]
    public void The_switch_is_a_bool_field_that_is_off_when_missing()
    {
        using var factory = new TodoistFactory(new FakeHandler());

        var field = factory.ConfigSchema.Fields.Single(f => f.Key == "allowChanges");

        Assert.Equal(FieldKind.Bool, field.Kind);
        Assert.False(field.Required);
        Assert.False(field.IsSecretField);
    }

    [Fact]
    public async Task Completing_sends_item_close_with_the_change_id_as_uuid_and_the_token_as_bearer()
    {
        var id = Guid.NewGuid();
        var (handler, source, factory) = Create((_, _) => FakeHandler.Json(Status(id, "\"ok\"")));
        using var _ = factory;

        var result = await ((IWritableTaskSource)source).CompleteAsync(Task42, id, Ct);

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        var command = Commands(handler).EnumerateArray().Single();
        Assert.Equal("item_close", command.GetProperty("type").GetString());
        Assert.Equal(id.ToString("D"), command.GetProperty("uuid").GetString());
        Assert.Equal("42", command.GetProperty("args").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Reopening_sends_item_uncomplete()
    {
        var id = Guid.NewGuid();
        var (handler, source, factory) = Create((_, _) => FakeHandler.Json(Status(id, "\"ok\"")));
        using var _ = factory;

        await ((IWritableTaskSource)source).ReopenAsync(Task42, id, Ct);

        Assert.Equal("item_uncomplete", Commands(handler).EnumerateArray().Single().GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_retry_sends_the_same_uuid_so_todoist_can_ignore_the_repeat()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var (handler, source, factory) = Create((_, _) => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : FakeHandler.Json(Status(id, "\"ok\"")));
        using var _ = factory;
        var writable = (IWritableTaskSource)source;

        await Assert.ThrowsAsync<SourceUnavailableException>(() => writable.CompleteAsync(Task42, id, Ct));
        await writable.CompleteAsync(Task42, id, Ct);

        Assert.Equal(
            [id.ToString("D"), id.ToString("D")],
            Enumerable.Range(0, 2).Select(i => Commands(handler, i).EnumerateArray().Single().GetProperty("uuid").GetString()));
    }

    [Fact]
    public async Task A_task_that_is_gone_is_reported_as_not_found()
    {
        var id = Guid.NewGuid();
        var (_, source, factory) = Create((_, _) => FakeHandler.Json(Status(id, """{"error_code":22,"error":"Item not found"}""")));
        using var _ = factory;

        var result = await ((IWritableTaskSource)source).CompleteAsync(Task42, id, Ct);

        Assert.Equal(WriteOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Any_other_error_throws_a_source_exception_with_a_short_one_line_text()
    {
        var id = Guid.NewGuid();
        var (_, source, factory) = Create((_, _) => FakeHandler.Json(Status(id, "{\"error_code\":15,\"error\":\"Bad\\nthing " + new string('x', 400) + "\"}")));
        using var _ = factory;

        var ex = await Assert.ThrowsAsync<SourceException>(() => ((IWritableTaskSource)source).CompleteAsync(Task42, id, Ct));

        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 200);
        Assert.Contains("15", ex.Diagnostics);
        Assert.DoesNotContain(TodoistTests.Secret, ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_without_a_result_for_the_command_is_a_data_error()
    {
        var (_, source, factory) = Create((_, _) => FakeHandler.Json("""{"sync_token":"t2","projects":[]}"""));
        using var _ = factory;

        await Assert.ThrowsAsync<SourceDataException>(() => ((IWritableTaskSource)source).CompleteAsync(Task42, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Auth_and_rate_limit_failures_use_the_normal_exceptions_without_the_token()
    {
        var (_, rejected, f1) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (_, limited, f2) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var _1 = f1;
        using var _2 = f2;

        var auth = await Assert.ThrowsAsync<SourceAuthException>(() => ((IWritableTaskSource)rejected).CompleteAsync(Task42, Guid.NewGuid(), Ct));
        var slow = await Assert.ThrowsAsync<SourceUnavailableException>(() => ((IWritableTaskSource)limited).CompleteAsync(Task42, Guid.NewGuid(), Ct));

        Assert.DoesNotContain(TodoistTests.Secret, auth.Message + auth.Diagnostics + slow.Message + slow.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_of_another_source_is_refused_before_anything_is_sent()
    {
        var (handler, source, factory) = Create((_, _) => FakeHandler.Json("{}"));
        using var _ = factory;

        await Assert.ThrowsAsync<SourceException>(() => ((IWritableTaskSource)source).CompleteAsync(new ItemKey("other", "p1", "42"), Guid.NewGuid(), Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_write_makes_the_next_pull_ask_again_instead_of_using_a_remembered_reply()
    {
        var id = Guid.NewGuid();
        var pulls = 0;
        var (handler, source, factory) = Create((_, body) =>
        {
            if (body.Contains("commands=", StringComparison.Ordinal))
            {
                return FakeHandler.Json(Status(id, "\"ok\""));
            }

            pulls++;
            return FakeHandler.Json("""{"sync_token":"t1","full_sync":true,"projects":[{"id":"p1","name":"Inbox"}],"items":[]}""");
        });
        using var _ = factory;
        var collection = new CollectionInfo("td-1", "p1", "Inbox", CollectionKind.TaskList);
        var tasks = (ITaskSource)source;

        await tasks.SyncTasksAsync(collection, null, Ct);
        await ((IWritableTaskSource)source).CompleteAsync(Task42, id, Ct);
        await tasks.SyncTasksAsync(collection, null, Ct);

        Assert.Equal(2, pulls);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Through_the_registry_the_switch_is_stored_as_plain_config_and_changes_what_the_source_can_do()
    {
        using var factory = new TodoistFactory(new FakeHandler());
        var store = new InMemoryStore();
        var registry = new SourceRegistry([factory], store, new InMemorySecretStore());

        var info = await registry.AddAsync("todoist", "Todoist", new Dictionary<string, string> { ["token"] = TodoistTests.Secret, ["allowChanges"] = "true" }, Ct);

        Assert.Equal("true", (await store.GetSourceConfigAsync(info.Id, Ct))!.Get("allowChanges"));
        Assert.IsAssignableFrom<IWritableTaskSource>(await registry.GetAsync(info, Ct));
    }
}
