using System.Net;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Microsoft.Tests;

public class TodoTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    internal const string Delta1 = "https://graph.microsoft.com/v1.0/me/todo/lists/list1/tasks/delta?$deltatoken=d1";
    internal const string Delta2 = "https://graph.microsoft.com/v1.0/me/todo/lists/list1/tasks/delta?$deltatoken=d2";

    private static string Task(string id, string extra = "", string title = "Task") =>
        $$"""{"id":"{{id}}","title":"{{title}}","status":"notStarted","importance":"normal"{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static async Task<(TaskItem Task, SyncResult<TaskItem> Result)> MapOneAsync(string extra)
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(Task("t1", extra), delta: Delta1));
        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);
        return (result.Upserts[0], result);
    }

    [Fact]
    public async Task Lists_are_read_only_task_lists_with_flagged_email_last()
    {
        var (_, source, _, _) = Setup.Create(
            _ => Setup.Page("""{"id":"f","displayName":"Flagged email","wellknownListName":"flaggedEmails"},{"id":"a","displayName":"Tasks","wellknownListName":"defaultList"},{"id":"b","displayName":"Shopping"}"""),
            calendar: false);

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["Tasks", "Shopping", "Flagged email"], collections.Select(c => c.DisplayName));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.Kind == CollectionKind.TaskList && c.Color is null));
    }

    [Fact]
    public async Task A_source_with_both_features_lists_calendars_then_lists()
    {
        var (_, source, _, _) = Setup.Create(r => r.RequestUri!.AbsolutePath.EndsWith("calendars", StringComparison.Ordinal)
            ? Setup.Page("""{"id":"c","name":"Calendar"}""")
            : Setup.Page("""{"id":"l","displayName":"Tasks"}"""));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal([CollectionKind.Calendar, CollectionKind.TaskList], collections.Select(c => c.Kind));
    }

    [Theory]
    [InlineData("notStarted", TaskItemStatus.Open)]
    [InlineData("deferred", TaskItemStatus.Open)]
    [InlineData("inProgress", TaskItemStatus.InProgress)]
    [InlineData("waitingOnOthers", TaskItemStatus.InProgress)]
    [InlineData("completed", TaskItemStatus.Completed)]
    public void Statuses_map_to_the_model(string status, TaskItemStatus expected) =>
        Assert.Equal(expected, TodoMapper.MapStatus(status));

    [Fact]
    public async Task An_unknown_status_is_open_with_a_warning()
    {
        var (mapped, result) = await MapOneAsync("\"status\":\"someFutureValue\"");

        Assert.Equal(TaskItemStatus.Open, mapped.Status);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Importance_completion_labels_notes_and_link_are_mapped()
    {
        var extra = """
            "importance":"high","status":"completed","completedDateTime":{"dateTime":"2026-10-20T08:15:00.0000000","timeZone":"UTC"},
            "categories":["Home"],"body":{"contentType":"text","content":"Buy milk"},"lastModifiedDateTime":"2026-10-20T08:16:00Z"
            """;

        var (mapped, _) = await MapOneAsync(extra);

        Assert.Equal(Priority.High, mapped.Priority);
        Assert.Equal(TaskItemStatus.Completed, mapped.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 0, TimeSpan.Zero), mapped.CompletedAt);
        Assert.Equal(["Home"], mapped.Labels);
        Assert.Equal("Buy milk", mapped.Notes);
        Assert.Equal("https://to-do.live.com/tasks/id/t1/details", mapped.Url?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 16, 0, TimeSpan.Zero), mapped.LastModified);
    }

    [Fact]
    public async Task Low_importance_is_low_and_normal_is_none()
    {
        var (low, _) = await MapOneAsync("\"importance\":\"low\"");
        var (normal, _) = await MapOneAsync("\"importance\":\"normal\"");

        Assert.Equal(Priority.Low, low.Priority);
        Assert.Equal(Priority.None, normal.Priority);
    }

    [Fact]
    public async Task An_html_body_becomes_plain_text()
    {
        var html = "<html><head><style>p{color:red}</style></head><body><p>Hello &amp; welcome</p><div>Second<br>line</div></body></html>";
        var (mapped, _) = await MapOneAsync($"\"body\":{{\"contentType\":\"html\",\"content\":\"{html}\"}}");

        Assert.Equal("Hello & welcome\nSecond\nline", mapped.Notes);
    }

    [Fact]
    public async Task A_due_date_at_midnight_is_date_only_whatever_the_zone()
    {
        var (mapped, _) = await MapOneAsync("\"dueDateTime\":{\"dateTime\":\"2026-10-30T00:00:00.0000000\",\"timeZone\":\"Pacific Standard Time\"}");

        Assert.Equal(new DueDate(new DateOnly(2026, 10, 30)), mapped.Due);
    }

    [Fact]
    public async Task A_due_time_is_a_moment_in_its_own_zone()
    {
        var (mapped, _) = await MapOneAsync("\"dueDateTime\":{\"dateTime\":\"2026-10-30T17:00:00.0000000\",\"timeZone\":\"W. Europe Standard Time\"}");

        var due = Assert.IsType<DueDateTime>(mapped.Due);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.Zero), due.At);
        Assert.Equal("Europe/Berlin", due.TimeZoneId);
    }

    [Fact]
    public async Task An_unknown_due_zone_assumes_utc_and_warns()
    {
        var (mapped, result) = await MapOneAsync("\"dueDateTime\":{\"dateTime\":\"2026-10-30T17:00:00.0000000\",\"timeZone\":\"Mars Standard Time\"}");

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero), Assert.IsType<DueDateTime>(mapped.Due).At);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task A_start_date_is_the_start_of_that_day_in_the_display_zone()
    {
        var (mapped, _) = await MapOneAsync("\"startDateTime\":{\"dateTime\":\"2026-10-30T00:00:00.0000000\",\"timeZone\":\"UTC\"}");

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.FromHours(1)), mapped.Start);
    }

    [Fact]
    public async Task A_task_with_an_unreadable_date_is_skipped_with_a_warning()
    {
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(
            Task("bad", "\"dueDateTime\":{\"dateTime\":\"nonsense\",\"timeZone\":\"UTC\"}") + "," + Task("ok"),
            delta: Delta1));

        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);

        Assert.Equal(["ok"], result.Upserts.Select(t => t.Key.ItemId));
        Assert.Equal("bad", Assert.Single(result.Warnings).ItemId);
    }

    [Theory]
    [InlineData("daily", "\"interval\":2", "FREQ=DAILY;INTERVAL=2")]
    [InlineData("weekly", "\"interval\":1,\"daysOfWeek\":[\"monday\",\"friday\"],\"firstDayOfWeek\":\"sunday\"", "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,FR;WKST=SU")]
    [InlineData("absoluteMonthly", "\"interval\":1,\"dayOfMonth\":15", "FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15")]
    [InlineData("relativeMonthly", "\"interval\":3,\"daysOfWeek\":[\"tuesday\"],\"index\":\"second\"", "FREQ=MONTHLY;INTERVAL=3;BYDAY=TU;BYSETPOS=2")]
    [InlineData("absoluteYearly", "\"interval\":1,\"month\":3,\"dayOfMonth\":9", "FREQ=YEARLY;INTERVAL=1;BYMONTH=3;BYMONTHDAY=9")]
    [InlineData("relativeYearly", "\"interval\":1,\"month\":11,\"daysOfWeek\":[\"thursday\"],\"index\":\"last\"", "FREQ=YEARLY;INTERVAL=1;BYMONTH=11;BYDAY=TH;BYSETPOS=-1")]
    public async Task Every_recurrence_pattern_becomes_an_rrule(string type, string fields, string expected)
    {
        var (mapped, _) = await MapOneAsync($"\"recurrence\":{{\"pattern\":{{\"type\":\"{type}\",{fields}}},\"range\":{{\"type\":\"noEnd\"}}}}");

        Assert.Equal(expected, mapped.Recurrence?.Rule);
    }

    [Fact]
    public async Task Recurrence_ranges_become_until_or_count()
    {
        var (until, _) = await MapOneAsync("\"recurrence\":{\"pattern\":{\"type\":\"daily\",\"interval\":1},\"range\":{\"type\":\"endDate\",\"endDate\":\"2026-12-31\"}}");
        var (count, _) = await MapOneAsync("\"recurrence\":{\"pattern\":{\"type\":\"daily\",\"interval\":1},\"range\":{\"type\":\"numbered\",\"numberOfOccurrences\":5}}");

        Assert.Equal("FREQ=DAILY;INTERVAL=1;UNTIL=20261231", until.Recurrence?.Rule);
        Assert.Equal("FREQ=DAILY;INTERVAL=1;COUNT=5", count.Recurrence?.Rule);
    }

    [Fact]
    public async Task An_unknown_recurrence_leaves_it_out_and_warns()
    {
        var (mapped, result) = await MapOneAsync("\"recurrence\":{\"pattern\":{\"type\":\"hijriMonthly\",\"interval\":1},\"range\":{\"type\":\"noEnd\"}}");

        Assert.Null(mapped.Recurrence);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Checklist_items_become_subtasks_of_their_task()
    {
        var checklist = """
            "checklistItems":[{"id":"c1","displayName":"Eggs","isChecked":false},{"id":"c2","displayName":"Milk","isChecked":true,"checkedDateTime":"2026-10-20T08:00:00Z"}]
            """;
        var (_, source, _, _) = Setup.Create(_ => Setup.Page(Task("t1", checklist), delta: Delta1));

        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);

        Assert.Equal(["t1", "t1/c1", "t1/c2"], result.Upserts.Select(t => t.Key.ItemId));
        var milk = result.Upserts[2];
        Assert.Equal(new ItemKey(Setup.SourceId, "list1", "t1"), milk.ParentKey);
        Assert.Equal(TaskItemStatus.Completed, milk.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), milk.CompletedAt);
    }

    [Fact]
    public async Task The_first_request_expands_checklist_items_and_a_later_one_follows_the_delta_link()
    {
        var phase = 0;
        var (handler, source, _, time) = Setup.Create(_ => Setup.Page(string.Empty, delta: phase == 0 ? Delta1 : Delta2));
        var first = await source.SyncTasksAsync(Setup.List(), null, Ct);
        phase = 1;
        time.Advance(TimeSpan.FromHours(1));

        await source.SyncTasksAsync(Setup.List(), first.NewState, Ct);

        Assert.True(first.IsFullSnapshot);
        Assert.Contains("expand=checklistItems", handler.GraphRequests.First().RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(Delta1, handler.GraphRequests.Last().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_removed_checklist_item_and_a_removed_task_are_reported_as_deleted()
    {
        const string Two = "\"checklistItems\":[{\"id\":\"c1\",\"displayName\":\"A\"},{\"id\":\"c2\",\"displayName\":\"B\"}]";
        const string One = "\"checklistItems\":[{\"id\":\"c1\",\"displayName\":\"A\"}]";
        const string Other = "\"checklistItems\":[{\"id\":\"c9\",\"displayName\":\"Z\"}]";
        var phase = 0;
        var (_, source, _, time) = Setup.Create(_ => phase switch
        {
            0 => Setup.Page(Task("t1", Two) + "," + Task("t2", Other), delta: Delta1),
            1 => Setup.Page(Task("t1", One), delta: Delta2),
            _ => Setup.Page("""{"id":"t2","@removed":{"reason":"deleted"}}""", delta: Delta2),
        });
        var first = await source.SyncTasksAsync(Setup.List(), null, Ct);
        phase = 1;
        time.Advance(TimeSpan.FromHours(1));
        var second = await source.SyncTasksAsync(Setup.List(), first.NewState, Ct);
        phase = 2;
        time.Advance(TimeSpan.FromHours(1));

        var third = await source.SyncTasksAsync(Setup.List(), second.NewState, Ct);

        Assert.Equal(["t1/c2"], second.DeletedItemIds);
        Assert.Equal(["t2", "t2/c9"], third.DeletedItemIds.Order());
    }

    [Fact]
    public async Task A_full_snapshot_is_taken_again_after_a_day()
    {
        var (handler, source, _, time) = Setup.Create(_ => Setup.Page(Task("t1"), delta: Delta1));
        var first = await source.SyncTasksAsync(Setup.List(), null, Ct);
        time.Advance(TimeSpan.FromHours(25));

        var second = await source.SyncTasksAsync(Setup.List(), first.NewState, Ct);

        Assert.True(second.IsFullSnapshot);
        Assert.Contains("expand=checklistItems", handler.GraphRequests.Last().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graph_errors_map_to_the_provider_exceptions()
    {
        foreach (var (status, expected) in new (HttpStatusCode, Type)[]
        {
            (HttpStatusCode.Unauthorized, typeof(SourceAuthException)),
            (HttpStatusCode.Forbidden, typeof(SourceAuthException)),
            (HttpStatusCode.InternalServerError, typeof(SourceUnavailableException)),
            (HttpStatusCode.BadRequest, typeof(SourceException)),
            (HttpStatusCode.Found, typeof(SourceDataException)),
        })
        {
            var (_, source, _, _) = Setup.Create(_ => new HttpResponseMessage(status), calendar: false);
            var ex = await Assert.ThrowsAnyAsync<SourceException>(() => source.SyncTasksAsync(Setup.List(), null, Ct));
            Assert.Equal(expected, ex.GetType());
        }
    }

    [Fact]
    public async Task A_mailbox_that_graph_cannot_read_is_a_plain_error_not_a_missing_list()
    {
        var (_, source, _, _) = Setup.Create(_ => FakeHandler.Json("""{"error":{"code":"MailboxNotEnabledForRESTAPI","message":"secret@example.com"}}""", HttpStatusCode.NotFound), calendar: false);

        var ex = await Assert.ThrowsAsync<SourceException>(() => source.SyncTasksAsync(Setup.List(), null, Ct));

        Assert.DoesNotContain("secret@example.com", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("MailboxNotEnabledForRESTAPI", ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throttled_request_waits_the_requested_time_inside_the_client_and_then_succeeds()
    {
        var calls = 0;
        var (_, source, _, _) = Setup.Create(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new(TimeSpan.Zero);
                return throttled;
            }

            return Setup.Page(Task("t1"), delta: Delta1);
        });

        var result = await source.SyncTasksAsync(Setup.List(), null, Ct);

        Assert.Single(result.Upserts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_long_throttle_is_passed_to_the_engine_with_the_servers_wait()
    {
        var (_, source, _, _) = Setup.Create(_ =>
        {
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return throttled;
        });

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Setup.List(), null, Ct));

        Assert.Equal(TimeSpan.FromMinutes(10), ex.RetryAfter);
    }
}
