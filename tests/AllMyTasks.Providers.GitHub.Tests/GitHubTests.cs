using System.Net;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.GitHub.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

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

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }
}

public class GitHubTests
{
    internal const string Secret = "github_pat_SECRET_123";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string Issue(long id, int number, string repo = "octo/app", string title = "Fix crash", string extra = "") =>
        $$"""{"id":{{id}},"number":{{number}},"title":"{{title}}","state":"open","html_url":"https://github.com/{{repo}}/issues/{{number}}","repository_url":"https://api.github.com/repos/{{repo}}","labels":[],"updated_at":"2026-10-30T10:00:00Z"{{(extra.Length == 0 ? "" : "," + extra)}}}""";

    private static string Search(bool incomplete, params string[] items) =>
        $$"""{"total_count":{{items.Length}},"incomplete_results":{{(incomplete ? "true" : "false")}},"items":[{{string.Join(',', items)}}]}""";

    private static bool IsSearch(HttpRequestMessage r) => r.RequestUri!.AbsolutePath == "/search/issues";

    private static (FakeHandler Handler, GitHubSource Source) Create(string assigned, string search) =>
        Create(r => FakeHandler.Json(IsSearch(r) ? search : assigned));

    private static (FakeHandler Handler, GitHubSource Source) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider? time = null)
    {
        var handler = new FakeHandler { Respond = respond };
        var client = new GitHubClient(handler, _ => Task.FromResult<string?>(Secret));
        return (handler, new GitHubSource("gh-1", "GitHub", client, timeProvider: time));
    }

    private static CollectionInfo Collection(string repo) => new("gh-1", repo, repo, CollectionKind.TaskList);

    private static async Task<TaskItem> MapOneAsync(string issue, bool review = false)
    {
        var (_, source) = review ? Create("[]", Search(false, issue)) : Create($"[{issue}]", Search(false));
        var result = await source.SyncTasksAsync(Collection("octo/app"), null, Ct);
        return Assert.Single(result.Upserts);
    }

    [Fact]
    public async Task Collections_are_the_repositories_that_have_items_sorted_and_read_only()
    {
        var (_, source) = Create($"[{Issue(1, 1, "zed/last")},{Issue(2, 2, "alpha/first")},{Issue(3, 3, "alpha/first")}]", Search(false, Issue(4, 4, "mid/repo")));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["alpha/first", "mid/repo", "zed/last"], collections.Select(c => c.Id));
        Assert.All(collections, c =>
        {
            Assert.True(c.IsReadOnly);
            Assert.Equal(CollectionKind.TaskList, c.Kind);
            Assert.Equal(c.Id, c.DisplayName);
        });
    }

    [Fact]
    public async Task A_sync_returns_the_items_of_that_repository_as_a_confirmed_snapshot()
    {
        var (_, source) = Create($"[{Issue(1, 1, "a/one")},{Issue(2, 2, "b/two")}]", Search(false));

        var result = await source.SyncTasksAsync(Collection("a/one"), null, Ct);

        Assert.True(result.IsFullSnapshot);
        var task = Assert.Single(result.Upserts);
        Assert.Equal(new ItemKey("gh-1", "a/one", "1"), task.Key);
        Assert.Equal("Fix crash (#1)", task.Title);
        Assert.Equal(new Uri("https://github.com/a/one/issues/1"), task.Url);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), task.LastModified);
        Assert.Equal(TaskItemStatus.Open, task.Status);
        Assert.Equal(Priority.None, task.Priority);
        Assert.True(result.ConfirmedEmpty);
    }

    [Fact]
    public async Task A_repository_with_no_items_any_more_syncs_to_an_empty_confirmed_snapshot()
    {
        var (_, source) = Create("[]", Search(false));

        var result = await source.SyncTasksAsync(Collection("gone/repo"), new SyncState("old"), Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Empty(result.Upserts);
        Assert.True(result.ConfirmedEmpty);
    }

    [Fact]
    public async Task Collections_synced_together_share_one_fetch()
    {
        var (handler, source) = Create($"[{Issue(1, 1, "a/one")},{Issue(2, 2, "b/two")}]", Search(false));

        await source.GetCollectionsAsync(Ct);
        await source.SyncTasksAsync(Collection("a/one"), null, Ct);
        await source.SyncTasksAsync(Collection("b/two"), null, Ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_pull_request_that_is_assigned_and_awaits_review_appears_once_with_the_review_label()
    {
        var pr = Issue(7, 7, extra: "\"pull_request\":{}");
        var (_, source) = Create($"[{pr}]", Search(false, pr));

        var result = await source.SyncTasksAsync(Collection("octo/app"), null, Ct);

        var task = Assert.Single(result.Upserts);
        Assert.Contains("review requested", task.Labels);
    }

    [Fact]
    public async Task A_review_request_gets_the_label_and_an_assigned_item_does_not()
    {
        var review = await MapOneAsync(Issue(1, 1, extra: "\"pull_request\":{}"), review: true);
        var assigned = await MapOneAsync(Issue(2, 2));

        Assert.Equal(["review requested"], review.Labels);
        Assert.Empty(assigned.Labels);
    }

    [Fact]
    public async Task Labels_and_draft_are_kept_as_labels()
    {
        var task = await MapOneAsync(Issue(1, 1, extra: "\"draft\":true,\"labels\":[{\"name\":\"bug\"},{\"name\":\"P1\"},{}]"));

        Assert.Equal(["bug", "P1", "draft"], task.Labels);
        Assert.Equal(Priority.None, task.Priority);
    }

    [Fact]
    public async Task A_milestone_due_date_becomes_a_date_only_due_and_no_milestone_means_undated()
    {
        var dated = await MapOneAsync(Issue(1, 1, extra: "\"milestone\":{\"due_on\":\"2026-11-15T08:00:00Z\"}"));
        var undated = await MapOneAsync(Issue(2, 2, extra: "\"milestone\":{\"due_on\":null}"));
        var none = await MapOneAsync(Issue(3, 3));

        Assert.Equal(new DueDate(new DateOnly(2026, 11, 15)), dated.Due);
        Assert.Null(undated.Due);
        Assert.Null(none.Due);
    }

    [Fact]
    public async Task Closed_and_merged_items_are_completed()
    {
        var closed = GitHubMapper_Map(Issue(1, 1).Replace("\"state\":\"open\"", "\"state\":\"closed\",\"closed_at\":\"2026-10-31T09:00:00Z\""));
        var merged = GitHubMapper_Map(Issue(2, 2, extra: "\"pull_request\":{\"merged_at\":\"2026-10-31T09:00:00Z\"},\"closed_at\":\"2026-10-31T09:00:00Z\"").Replace("\"state\":\"open\"", "\"state\":\"closed\""));
        await Task.CompletedTask;

        Assert.Equal(TaskItemStatus.Completed, closed.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture), closed.CompletedAt);
        Assert.Equal(TaskItemStatus.Completed, merged.Status);
    }

    private static TaskItem GitHubMapper_Map(string json)
    {
        var dto = Assert.Single(GitHubClient.ParseIssues(Encoding.UTF8.GetBytes($"[{json}]")));
        return Assert.IsType<WorkItem>(new GitHubMapper("gh-1").Map(dto, false)).Task;
    }

    [Fact]
    public async Task The_repository_comes_from_the_repository_object_when_present()
    {
        var (_, source) = Create($"[{Issue(1, 1, "x/y", extra: "\"repository\":{\"full_name\":\"owner/name\"}")}]", Search(false));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal("owner/name", Assert.Single(collections).Id);
    }

    [Fact]
    public async Task Items_that_cannot_be_read_are_skipped_with_a_warning_and_a_bad_repository_name_is_refused()
    {
        var noRepo = """{"id":5,"number":5,"title":"No repo","state":"open"}""";
        var badRepo = Issue(6, 6, "bad name/with space");
        var (_, source) = Create($"[{noRepo},{badRepo},{Issue(1, 1)}]", Search(false));

        var result = await source.SyncTasksAsync(Collection("octo/app"), null, Ct);

        Assert.Single(result.Upserts);
        Assert.Equal(["5", "6"], result.Warnings.Select(w => w.ItemId));
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("No repo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://github.com/a/b/issues/1")]
    [InlineData("https://evil.example/a/b/issues/1")]
    [InlineData("https://github.com.evil.example/a/b/issues/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void Only_https_github_addresses_are_kept_as_links(string url)
    {
        Assert.Null(GitHubMapper.SafeUrl(url));
        Assert.NotNull(GitHubMapper.SafeUrl("https://github.com/a/b/pull/1"));
    }

    [Fact]
    public async Task The_request_is_authenticated_versioned_and_asks_for_open_assigned_items_and_review_requests()
    {
        var (handler, source) = Create("[]", Search(false));

        await source.GetCollectionsAsync(Ct);

        var assigned = handler.Requests.Single(r => !IsSearch(r));
        Assert.Equal("api.github.com", assigned.RequestUri!.Host);
        Assert.Equal("/issues", assigned.RequestUri.AbsolutePath);
        Assert.Contains("filter=assigned", assigned.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("state=open", assigned.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer", assigned.Headers.Authorization!.Scheme);
        Assert.Equal(Secret, assigned.Headers.Authorization.Parameter);
        Assert.Equal("2022-11-28", assigned.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Contains("application/vnd.github+json", assigned.Headers.Accept.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(assigned.Headers.UserAgent);

        var review = handler.Requests.Single(IsSearch);
        Assert.Equal("is:pr is:open review-requested:@me archived:false", Uri.UnescapeDataString(review.RequestUri!.Query).Split("q=")[1].Split('&')[0].Replace('+', ' '));
    }

    [Fact]
    public async Task Paging_continues_while_a_page_is_full_and_stops_at_the_first_short_one()
    {
        var full = string.Join(',', Enumerable.Range(1, GitHubClient.PageSize).Select(i => Issue(i, i)));
        var (handler, source) = Create(r =>
        {
            if (IsSearch(r))
            {
                return FakeHandler.Json(Search(false));
            }

            return FakeHandler.Json(r.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal) ? $"[{full}]" : $"[{Issue(1000, 1000)}]");
        });

        var result = await source.SyncTasksAsync(Collection("octo/app"), null, Ct);

        Assert.Equal(GitHubClient.PageSize + 1, result.Upserts.Count);
        Assert.Equal(2, handler.Requests.Count(r => !IsSearch(r)));
    }

    [Fact]
    public async Task A_not_modified_answer_reuses_the_remembered_reply_with_if_none_match()
    {
        var calls = 0;
        var time = new ManualTime();
        var (handler, source) = Create(
            r =>
            {
                if (IsSearch(r))
                {
                    return FakeHandler.Json(Search(false));
                }

                calls++;
                return calls == 1 ? FakeHandler.Json($"[{Issue(1, 1)}]", etag: "\"abc\"") : new HttpResponseMessage(HttpStatusCode.NotModified);
            },
            time);
        var first = await source.SyncTasksAsync(Collection("octo/app"), null, Ct);
        time.Now = time.Now.AddMinutes(5);

        var second = await source.SyncTasksAsync(Collection("octo/app"), first.NewState, Ct);

        Assert.Single(second.Upserts);
        var conditional = handler.Requests.Where(r => !IsSearch(r)).ElementAt(1);
        Assert.Equal("\"abc\"", conditional.Headers.IfNoneMatch.Single().Tag);
    }

    [Fact]
    public async Task An_incomplete_search_is_not_used_so_nothing_is_wiped()
    {
        var (_, source) = Create($"[{Issue(1, 1)}]", Search(true));

        var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection("octo/app"), null, Ct));

        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_rejected_token_is_an_auth_error_that_does_not_leak_the_token(int status)
    {
        var (_, source) = Create(_ => FakeHandler.Json("""{"message":"Bad credentials"}""", (HttpStatusCode)status));

        var error = await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.DoesNotContain(Secret, error.Message + error.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_is_an_auth_error()
    {
        var factory = new GitHubFactory(new FakeHandler());
        var source = (GitHubSource)factory.Create("gh-2", "GitHub", SourceConfig.Empty, new InMemorySecretStore());

        await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));
    }

    [Fact]
    public async Task A_rate_limit_is_unavailable_with_the_wait_the_server_asked_for()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var (_, limited) = Create(_ =>
        {
            var response = FakeHandler.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });
        var (_, secondary) = Create(_ =>
        {
            var response = FakeHandler.Json("""{"message":"secondary rate limit"}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
            return response;
        });

        var primaryError = await Assert.ThrowsAsync<SourceUnavailableException>(() => limited.GetCollectionsAsync(Ct));
        var secondaryError = await Assert.ThrowsAsync<SourceUnavailableException>(() => secondary.GetCollectionsAsync(Ct));

        Assert.InRange(primaryError.RetryAfter!.Value.TotalMinutes, 9, 10);
        Assert.Equal(TimeSpan.FromSeconds(90), secondaryError.RetryAfter);
    }

    [Fact]
    public async Task Server_errors_are_unavailable_and_redirects_are_data_errors()
    {
        var (_, broken) = Create(_ => FakeHandler.Json("{}", HttpStatusCode.BadGateway));
        var (_, redirected) = Create(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } });

        await Assert.ThrowsAsync<SourceUnavailableException>(() => broken.GetCollectionsAsync(Ct));
        var error = await Assert.ThrowsAsync<SourceDataException>(() => redirected.GetCollectionsAsync(Ct));
        Assert.DoesNotContain("evil", error.Message + error.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_or_oversized_reply_is_a_data_error_without_the_content()
    {
        var (_, garbage) = Create(_ => FakeHandler.Json("SECRET-BODY not json"));
        var handler = new FakeHandler { Respond = _ => FakeHandler.Json(new string('x', 200)) };
        using var client = new GitHubClient(handler, _ => Task.FromResult<string?>(Secret), maxBytes: 100);

        var error = await Assert.ThrowsAsync<SourceDataException>(() => garbage.GetCollectionsAsync(Ct));
        var large = await Assert.ThrowsAsync<SourceDataException>(() => client.GetAssignedAsync(Ct));

        Assert.DoesNotContain("SECRET-BODY", error.Message + error.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("larger", large.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_remembered()
    {
        var fail = true;
        var (handler, source) = Create(r => fail ? FakeHandler.Json("{}", HttpStatusCode.BadGateway) : FakeHandler.Json(IsSearch(r) ? Search(false) : $"[{Issue(1, 1)}]"));
        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.GetCollectionsAsync(Ct));
        fail = false;

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Single(collections);
        Assert.True(handler.Requests.Count >= 2);
    }

    [Theory]
    [InlineData("ghp_abc123", true)]
    [InlineData("tok en", false)]
    [InlineData("tök", false)]
    [InlineData("", false)]
    public void Only_header_safe_tokens_are_used(string token, bool usable) =>
        Assert.Equal(usable, GitHubClient.IsUsableToken(token));

    [Fact]
    public void The_factory_describes_a_secret_token_field_and_creates_a_read_only_github_source()
    {
        using var factory = new GitHubFactory(new FakeHandler());

        Assert.Equal("github", factory.Kind);
        var field = factory.ConfigSchema.Fields.Single(f => f.Key == "token");
        Assert.True(field.IsSecretField);
        Assert.True(field.Required);
        var source = factory.Create("gh-3", "Work GitHub", SourceConfig.Empty, new InMemorySecretStore());
        Assert.IsAssignableFrom<ITaskSource>(source);
        Assert.IsNotAssignableFrom<IWritableTaskSource>(source);
        Assert.Equal("github", source.Kind);
    }
}
