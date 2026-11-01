using System.Net;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.GitLab.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Json(string json, string? nextPage = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-next-page", nextPage ?? string.Empty);
        return response;
    }
}

public class GitLabTests
{
    internal const string Secret = "glpat-SECRET-123";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string Issue(long id, long iid, long project = 7, string path = "acme/app", string extra = "") =>
        $$"""{"id":{{id}},"iid":{{iid}},"project_id":{{project}},"title":"Fix {{id}}","state":"opened","labels":["bug"],"web_url":"https://gitlab.example.com/{{path}}/-/issues/{{iid}}","updated_at":"2026-10-30T10:00:00.000Z","references":{"full":"{{path}}#{{iid}}"}{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static string Mr(long id, long iid, long project = 7, string path = "acme/app", string extra = "") =>
        $$"""{"id":{{id}},"iid":{{iid}},"project_id":{{project}},"title":"Change {{id}}","state":"opened","labels":[],"draft":false,"web_url":"https://gitlab.example.com/{{path}}/-/merge_requests/{{iid}}","updated_at":"2026-10-30T10:00:00.000Z","references":{"full":"{{path}}!{{iid}}"}{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static string List(params string[] items) => "[" + string.Join(',', items) + "]";

    /// <summary>Answers the three lists by their query; <paramref name="reviewing"/> is the reviewer_username list.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Server(string issues = "[]", string assigned = "[]", string reviewing = "[]") => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        if (path.EndsWith("/api/v4/user", StringComparison.Ordinal))
        {
            return FakeHandler.Json("""{"username":"jane.doe"}""");
        }

        if (path.EndsWith("/api/v4/issues", StringComparison.Ordinal))
        {
            return FakeHandler.Json(issues);
        }

        if (path.EndsWith("/api/v4/merge_requests", StringComparison.Ordinal))
        {
            return FakeHandler.Json(query.Contains("reviewer_username", StringComparison.Ordinal) ? reviewing : assigned);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static (FakeHandler Handler, GitLabSource Source, GitLabFactory Factory) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string? baseUrl = "https://gitlab.example.com", TimeProvider? time = null)
    {
        var handler = new FakeHandler { Respond = respond };
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("gl-1", "token", Secret, Ct).GetAwaiter().GetResult();
        var factory = new GitLabFactory(handler);
        var config = new SourceConfig(baseUrl is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["baseUrl"] = baseUrl });
        var created = (GitLabSource)factory.Create("gl-1", "GitLab", config, secrets);
        return (handler, created, factory);
    }

    private static CollectionInfo Collection(string id = "7") => new("gl-1", id, "acme/app", CollectionKind.TaskList);

    [Fact]
    public async Task Collections_are_projects_with_items_named_by_path_and_read_only()
    {
        var (_, source, _) = Create(Server(
            issues: List(Issue(1, 1), Issue(2, 2, 8, "acme/tools/cli")),
            assigned: List(Mr(3, 1, 9, "zeta/web"))));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["acme/app", "acme/tools/cli", "zeta/web"], collections.Select(c => c.DisplayName));
        Assert.Equal(["7", "8", "9"], collections.Select(c => c.Id));
        Assert.All(collections, c => Assert.True(c.IsReadOnly && c.Kind == CollectionKind.TaskList && c.SourceId == "gl-1"));
    }

    [Fact]
    public async Task Sync_is_a_confirmed_snapshot_of_one_project_with_ids_titles_and_links()
    {
        var (_, source, _) = Create(Server(issues: List(Issue(1, 12), Issue(2, 3, 8, "acme/other"))));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.True(result.IsFullSnapshot && result.ConfirmedEmpty);
        var task = Assert.Single(result.Upserts);
        Assert.Equal(new ItemKey("gl-1", "7", "issue-1"), task.Key);
        Assert.Equal("Fix 1 (#12)", task.Title);
        Assert.Equal(["bug"], task.Labels);
        Assert.Equal(new Uri("https://gitlab.example.com/acme/app/-/issues/12"), task.Url);
        Assert.Equal(TaskItemStatus.Open, task.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), task.LastModified);
        Assert.Null(task.Due);
    }

    [Fact]
    public async Task A_project_without_items_any_more_is_an_empty_confirmed_snapshot()
    {
        var (_, source, _) = Create(Server());

        var result = await source.SyncTasksAsync(Collection(), new SyncState("old"), Ct);

        Assert.True(result.IsFullSnapshot && result.ConfirmedEmpty);
        Assert.Empty(result.Upserts);
    }

    [Fact]
    public async Task Issue_due_date_is_date_only_and_missing_means_undated()
    {
        var (_, source, _) = Create(Server(issues: List(Issue(1, 1, extra: "\"due_date\":\"2026-11-05\""), Issue(2, 2, extra: "\"due_date\":null"))));

        var tasks = (await source.SyncTasksAsync(Collection(), null, Ct)).Upserts;

        Assert.Equal(new DueDate(new DateOnly(2026, 11, 5)), tasks.Single(t => t.Key.ItemId == "issue-1").Due);
        Assert.Null(tasks.Single(t => t.Key.ItemId == "issue-2").Due);
    }

    [Fact]
    public async Task An_unreadable_due_date_skips_only_that_item_with_a_warning()
    {
        var (_, source, _) = Create(Server(issues: List(Issue(1, 1, extra: "\"due_date\":\"someday\""), Issue(2, 2))));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal("issue-2", Assert.Single(result.Upserts).Key.ItemId);
        Assert.Equal("issue-1", Assert.Single(result.Warnings).ItemId);
    }

    [Fact]
    public async Task Merge_request_takes_its_due_date_from_the_milestone_and_draft_becomes_a_label()
    {
        var (_, source, _) = Create(Server(assigned: List(
            Mr(1, 5, extra: "\"milestone\":{\"due_date\":\"2026-12-01\"},\"draft\":true"),
            Mr(2, 6))));

        var tasks = (await source.SyncTasksAsync(Collection(), null, Ct)).Upserts;

        var draft = tasks.Single(t => t.Key.ItemId == "mr-1");
        Assert.Equal("Change 1 (!5)", draft.Title);
        Assert.Equal(new DueDate(new DateOnly(2026, 12, 1)), draft.Due);
        Assert.Contains("draft", draft.Labels);
        var plain = tasks.Single(t => t.Key.ItemId == "mr-2");
        Assert.Null(plain.Due);
        Assert.DoesNotContain("draft", plain.Labels);
    }

    [Fact]
    public async Task Review_requests_are_included_labelled_and_not_duplicated_when_also_assigned()
    {
        var (handler, source, _) = Create(Server(
            assigned: List(Mr(1, 1), Mr(2, 2)),
            reviewing: List(Mr(2, 2), Mr(3, 3))));

        var tasks = (await source.SyncTasksAsync(Collection(), null, Ct)).Upserts;

        Assert.Equal(["mr-1", "mr-2", "mr-3"], tasks.Select(t => t.Key.ItemId).Order());
        Assert.DoesNotContain("review requested", tasks.Single(t => t.Key.ItemId == "mr-1").Labels);
        Assert.Contains("review requested", tasks.Single(t => t.Key.ItemId == "mr-2").Labels);
        Assert.Contains("review requested", tasks.Single(t => t.Key.ItemId == "mr-3").Labels);
        var reviewerRequest = Assert.Single(handler.Requests, r => r.RequestUri!.Query.Contains("reviewer_username", StringComparison.Ordinal));
        Assert.Contains("reviewer_username=jane.doe", reviewerRequest.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("scope=all", reviewerRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("state=opened", reviewerRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_and_merged_items_map_to_completed()
    {
        var (_, source, _) = Create(Server(issues: List(Issue(1, 1, extra: "\"state\":\"closed\"")), assigned: List(Mr(2, 2, extra: "\"state\":\"merged\""))));

        var tasks = (await source.SyncTasksAsync(Collection(), null, Ct)).Upserts;

        Assert.All(tasks, t => Assert.Equal(TaskItemStatus.Completed, t.Status));
    }

    [Fact]
    public async Task Collections_and_syncs_share_one_fetch_until_it_expires()
    {
        var time = new FakeTime(DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var handler = new FakeHandler { Respond = Server(issues: List(Issue(1, 1))) };
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync("gl-1", "token", Secret, Ct);
        using var client = new GitLabClient(new Uri("https://gitlab.example.com"), handler, _ => Task.FromResult<string?>(Secret));
        using var source = new GitLabSource("gl-1", "GitLab", client, timeProvider: time);

        await source.GetCollectionsAsync(Ct);
        await source.SyncTasksAsync(Collection(), null, Ct);
        var firstRound = handler.Requests.Count;
        Assert.Equal(4, firstRound);

        time.Advance(GitLabSource.MemoLifetime + TimeSpan.FromSeconds(1));
        await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal(firstRound * 2, handler.Requests.Count);
    }

    [Fact]
    public async Task Paging_follows_x_next_page_and_stops_when_it_is_empty()
    {
        var (handler, source, _) = Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/user", StringComparison.Ordinal))
            {
                return FakeHandler.Json("""{"username":"jane.doe"}""");
            }

            if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                return request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)
                    ? FakeHandler.Json(List(Issue(2, 2)))
                    : FakeHandler.Json(List(Issue(1, 1)), nextPage: "2");
            }

            return FakeHandler.Json("[]");
        });

        var tasks = (await source.SyncTasksAsync(Collection(), null, Ct)).Upserts;

        Assert.Equal(["issue-1", "issue-2"], tasks.Select(t => t.Key.ItemId).Order());
        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)));
        Assert.Contains("per_page=100", handler.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)).RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instance_with_a_path_prefix_keeps_it_and_the_token_goes_in_the_private_token_header_only()
    {
        var (handler, source, _) = Create(Server(), baseUrl: "https://example.com/gitlab/");

        await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.All(handler.Requests, r =>
        {
            Assert.StartsWith("https://example.com/gitlab/api/v4/", r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal(Secret, Assert.Single(r.Headers.GetValues("PRIVATE-TOKEN")));
            Assert.Null(r.Headers.Authorization);
            Assert.DoesNotContain(Secret, r.RequestUri.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Blank_address_means_gitlab_com()
    {
        var (handler, source, _) = Create(Server(), baseUrl: null);

        await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.All(handler.Requests, r => Assert.Equal("gitlab.com", r.RequestUri!.Host));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://gitlab.com", true)]
    [InlineData("https://git.example.com/gitlab", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://git.example.com", false)]
    [InlineData("ftp://git.example.com", false)]
    [InlineData("git.example.com", false)]
    [InlineData("https://user:pw@git.example.com", false)]
    [InlineData("https://git.example.com/?a=b", false)]
    public void Base_address_must_be_https_without_credentials(string text, bool valid)
    {
        var (_, _, factory) = Create(Server());

        var errors = factory.ValidateValues(new Dictionary<string, string> { ["baseUrl"] = text }).ToList();

        Assert.Equal(valid, errors.Count == 0);
        Assert.Equal(valid, GitLabClient.TryParseBaseUrl(text, out _));
    }

    [Fact]
    public void Form_has_the_address_and_a_secret_token()
    {
        var (_, _, factory) = Create(Server());

        Assert.Equal("gitlab", factory.Kind);
        var token = factory.ConfigSchema.Fields.Single(f => f.Key == "token");
        Assert.True(token.Required && token.IsSecretField);
        Assert.Contains(factory.ConfigSchema.Fields, f => f.Key == "baseUrl" && f.Kind == FieldKind.Url && !f.Required);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_token_is_an_auth_error_without_the_token_in_the_message(HttpStatusCode status)
    {
        var (_, source, _) = Create(_ => FakeHandler.Json("""{"message":"401 Unauthorized"}""", status: status));

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.DoesNotContain(Secret, ex.Message + ex.Diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("gitlab.example.com", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_requests_is_unavailable_and_carries_retry_after()
    {
        var (_, source, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(42));
            return response;
        });

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.GetCollectionsAsync(Ct));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task A_server_error_is_unavailable_and_a_redirect_is_never_followed()
    {
        var (_, failing, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<SourceUnavailableException>(() => failing.GetCollectionsAsync(Ct));

        var (_, redirecting, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Found));
        var ex = await Assert.ThrowsAsync<SourceDataException>(() => redirecting.GetCollectionsAsync(Ct));
        Assert.DoesNotContain(Secret, ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_replies_are_data_errors_that_do_not_quote_the_body()
    {
        var (_, source, _) = Create(_ => FakeHandler.Json("<html>secret page</html>"));

        var ex = await Assert.ThrowsAsync<SourceDataException>(() => source.GetCollectionsAsync(Ct));

        Assert.DoesNotContain("secret page", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_is_an_auth_error_and_nothing_is_sent()
    {
        var handler = new FakeHandler { Respond = Server() };
        var factory = new GitLabFactory(handler);
        using var source = (GitLabSource)factory.Create("gl-1", "GitLab", SourceConfig.Empty, new InMemorySecretStore());

        await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_remembered()
    {
        var calls = 0;
        var good = Server(issues: List(Issue(1, 1)));
        var (_, source, _) = Create(request => ++calls <= 1 ? new HttpResponseMessage(HttpStatusCode.BadRequest) : good(request));

        await Assert.ThrowsAsync<SourceException>(() => source.GetCollectionsAsync(Ct));
        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Single(collections);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
