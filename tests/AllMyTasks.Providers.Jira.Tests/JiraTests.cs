using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Providers.Jira.Tests;

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

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class JiraTests
{
    internal const string Secret = "tok_SECRET_123";
    internal const string Email = "me@example.com";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string Issue(string id, string key, string project = "10000", string summary = "Do it", string extra = "") =>
        $$$"""{"id":"{{{id}}}","key":"{{{key}}}","fields":{"summary":"{{{summary}}}","project":{"id":"{{{project}}}","key":"P{{{project}}}","name":"Project"}{{{(extra.Length == 0 ? "" : "," + extra)}}}}}""";

    private static string Page(string issues, string? next = null) =>
        next is null ? $$"""{"issues":[{{issues}}],"isLast":true}""" : $$"""{"issues":[{{issues}}],"nextPageToken":"{{next}}"}""";

    private static (FakeHandler Handler, JiraSource Source) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string site = "https://example.atlassian.net")
    {
        var handler = new FakeHandler { Respond = respond };
        var secrets = new InMemorySecretStore();
        secrets.SetAsync("jira-1", JiraFactory.TokenField, Secret, Ct).GetAwaiter().GetResult();
        var config = new SourceConfig(new Dictionary<string, string> { [JiraFactory.SiteField] = site, [JiraFactory.EmailField] = Email });
        return (handler, (JiraSource)new JiraFactory(handler).Create("jira-1", "Work Jira", config, secrets));
    }

    private static CollectionInfo Collection(string id = "10000") => new("jira-1", id, "PRJ Project", CollectionKind.TaskList);

    private static async Task<TaskItem> MapOneAsync(string extra, string summary = "Do it")
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(Issue("1", "PRJ-1", summary: summary, extra: extra))));
        var result = await source.SyncTasksAsync(Collection(), null, Ct);
        return Assert.Single(result.Upserts);
    }

    [Fact]
    public async Task Collections_are_the_projects_with_open_issues_read_only_and_named_by_key()
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(string.Join(',', Issue("1", "B-1", "20"), Issue("2", "A-1", "10"), Issue("3", "A-2", "10")))));

        var collections = await source.GetCollectionsAsync(Ct);

        Assert.Equal(["10", "20"], collections.Select(c => c.Id));
        Assert.All(collections, c => Assert.True(c.IsReadOnly));
        Assert.All(collections, c => Assert.Equal(CollectionKind.TaskList, c.Kind));
        Assert.Equal("P10 Project", collections[0].DisplayName);
    }

    [Fact]
    public async Task Issues_are_split_by_project_into_a_full_snapshot()
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(string.Join(',', Issue("1", "A-1", "10"), Issue("2", "B-1", "20")))));

        var result = await source.SyncTasksAsync(Collection("10"), null, Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.Equal(["1"], result.Upserts.Select(t => t.Key.ItemId));
        Assert.Equal("jira-1", result.Upserts[0].Key.SourceId);
    }

    [Fact]
    public async Task An_empty_result_is_a_confirmed_empty_snapshot()
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(string.Empty)));

        var result = await source.SyncTasksAsync(Collection(), new SyncState("old"), Ct);

        Assert.True(result.IsFullSnapshot);
        Assert.True(result.ConfirmedEmpty);
        Assert.Empty(result.Upserts);
    }

    [Fact]
    public async Task Title_is_the_key_and_summary_and_the_link_is_the_browse_page()
    {
        var task = await MapOneAsync(string.Empty, "Fix the thing");

        Assert.Equal("PRJ-1 Fix the thing", task.Title);
        Assert.Equal(new Uri("https://example.atlassian.net/browse/PRJ-1"), task.Url);
    }

    [Fact]
    public async Task Due_date_is_date_only_and_missing_means_no_due()
    {
        var withDue = await MapOneAsync("\"duedate\":\"2026-11-15\"");
        var without = await MapOneAsync("\"duedate\":null");

        Assert.Equal(new DueDate(new DateOnly(2026, 11, 15)), withDue.Due);
        Assert.Null(without.Due);
    }

    [Theory]
    [InlineData("Highest", Priority.High)]
    [InlineData("High", Priority.High)]
    [InlineData("Medium", Priority.Medium)]
    [InlineData("Low", Priority.Low)]
    [InlineData("Lowest", Priority.Low)]
    [InlineData("Whatever", Priority.None)]
    public async Task Priority_names_map_to_the_normalised_priority(string name, Priority expected)
    {
        var task = await MapOneAsync($$$"""
            "priority":{"name":"{{{name}}}"}
            """);

        Assert.Equal(expected, task.Priority);
    }

    [Theory]
    [InlineData("new", TaskItemStatus.Open)]
    [InlineData("indeterminate", TaskItemStatus.InProgress)]
    [InlineData("done", TaskItemStatus.Completed)]
    public async Task Status_category_maps_to_the_status(string category, TaskItemStatus expected)
    {
        var task = await MapOneAsync($$$"""
            "status":{"statusCategory":{"key":"{{{category}}}"}}
            """);

        Assert.Equal(expected, task.Status);
    }

    [Fact]
    public async Task Labels_and_updated_time_are_kept()
    {
        var task = await MapOneAsync("\"labels\":[\"a\",\"b\"],\"updated\":\"2026-10-31T12:34:56.789+0100\"");

        Assert.Equal(["a", "b"], task.Labels);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 11, 34, 56, 789, TimeSpan.Zero), task.LastModified);
    }

    [Fact]
    public async Task Description_in_document_format_is_flattened_to_plain_text()
    {
        const string Adf = """
            "description":{"type":"doc","version":1,"content":[
              {"type":"paragraph","content":[{"type":"text","text":"First "},{"type":"text","text":"line","marks":[{"type":"strong"}]}]},
              {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"item"}]}]}]}
            ]}
            """;

        var task = await MapOneAsync(Adf);

        Assert.Equal("First line\nitem", task.Notes);
    }

    [Fact]
    public async Task A_missing_or_empty_description_is_no_notes()
    {
        Assert.Null((await MapOneAsync("\"description\":null")).Notes);
        Assert.Null((await MapOneAsync(string.Empty)).Notes);
    }

    [Fact]
    public async Task A_parent_is_linked_only_when_it_is_part_of_the_result()
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(string.Join(',',
            Issue("1", "PRJ-1"),
            Issue("2", "PRJ-2", extra: "\"parent\":{\"id\":\"1\",\"key\":\"PRJ-1\"}"),
            Issue("3", "PRJ-3", extra: "\"parent\":{\"id\":\"99\",\"key\":\"PRJ-99\"}")))));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal("1", result.Upserts.Single(t => t.Key.ItemId == "2").ParentKey?.ItemId);
        Assert.Null(result.Upserts.Single(t => t.Key.ItemId == "3").ParentKey);
    }

    [Fact]
    public async Task An_unreadable_issue_is_skipped_with_a_warning()
    {
        var (_, source) = Create(_ => FakeHandler.Json(Page(string.Join(',', Issue("1", "PRJ-1"), Issue("2", "PRJ-2", extra: "\"duedate\":\"soon\"")))));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal(["1"], result.Upserts.Select(t => t.Key.ItemId));
        Assert.Equal("2", Assert.Single(result.Warnings).ItemId);
    }

    [Fact]
    public async Task Pages_are_followed_with_the_page_token_until_the_last()
    {
        var (handler, source) = Create(request => request.RequestUri!.Query.Contains("nextPageToken=tok%2F2", StringComparison.Ordinal)
            ? FakeHandler.Json(Page(Issue("2", "PRJ-2")))
            : FakeHandler.Json(Page(Issue("1", "PRJ-1"), "tok/2")));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Equal(["1", "2"], result.Upserts.Select(t => t.Key.ItemId));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_repeated_page_token_ends_the_paging()
    {
        var (handler, source) = Create(_ => FakeHandler.Json(Page(Issue("1", "PRJ-1"), "same")));

        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Single(result.Upserts);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_request_asks_for_open_assigned_issues_with_limited_fields_and_basic_auth()
    {
        var (handler, source) = Create(_ => FakeHandler.Json(Page(string.Empty)));

        await source.SyncTasksAsync(Collection(), null, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("example.atlassian.net", request.RequestUri!.Host);
        Assert.Equal("/rest/api/3/search/jql", request.RequestUri.AbsolutePath);
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        Assert.Contains("assignee = currentUser() AND statusCategory != Done", query, StringComparison.Ordinal);
        Assert.Contains("fields=summary,duedate,priority", query, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, request.Method);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Secret}"));
        Assert.Equal(new AuthenticationHeaderValue("Basic", expected), request.Headers.Authorization);
    }

    [Fact]
    public async Task Collections_synced_together_share_one_request()
    {
        var (handler, source) = Create(_ => FakeHandler.Json(Page(string.Join(',', Issue("1", "A-1", "10"), Issue("2", "B-1", "20")))));

        await source.GetCollectionsAsync(Ct);
        await source.SyncTasksAsync(Collection("10"), null, Ct);
        await source.SyncTasksAsync(Collection("20"), null, Ct);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_failed_request_is_not_remembered()
    {
        var calls = 0;
        var (_, source) = Create(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : FakeHandler.Json(Page(Issue("1", "PRJ-1"))));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));
        await Task.Delay(50, Ct);
        var result = await source.SyncTasksAsync(Collection(), null, Ct);

        Assert.Single(result.Upserts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_token_is_an_auth_error_that_does_not_leak_secrets(HttpStatusCode status)
    {
        var (_, source) = Create(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<SourceAuthException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        AssertNoSecrets(ex);
    }

    [Fact]
    public async Task Too_many_requests_is_unavailable_with_the_retry_after_wait()
    {
        var (_, source) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
        AssertNoSecrets(ex);
    }

    [Fact]
    public async Task A_redirect_is_refused_and_not_followed()
    {
        var (handler, source) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });

        var ex = await Assert.ThrowsAsync<SourceDataException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.Single(handler.Requests);
        AssertNoSecrets(ex);
    }

    [Fact]
    public async Task Server_errors_are_unavailable_and_not_found_names_the_site_problem()
    {
        var (_, down) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var (_, missing) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => down.SyncTasksAsync(Collection(), null, Ct));
        await Assert.ThrowsAsync<SourceDataException>(() => missing.SyncTasksAsync(Collection(), null, Ct));
    }

    [Fact]
    public async Task Invalid_json_is_a_data_error_without_the_body()
    {
        var (_, source) = Create(_ => FakeHandler.Json("<html>secret page</html>"));

        var ex = await Assert.ThrowsAsync<SourceDataException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        Assert.DoesNotContain("secret page", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_is_an_auth_error_and_nothing_is_sent()
    {
        var handler = new FakeHandler();
        var config = new SourceConfig(new Dictionary<string, string> { [JiraFactory.SiteField] = "https://example.atlassian.net", [JiraFactory.EmailField] = Email });
        var source = (JiraSource)new JiraFactory(handler).Create("jira-1", "Jira", config, new InMemorySecretStore());

        await Assert.ThrowsAsync<SourceAuthException>(() => source.GetCollectionsAsync(Ct));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable_and_does_not_name_the_address()
    {
        var (_, source) = Create(_ => throw new HttpRequestException("could not reach https://example.atlassian.net with " + Secret));

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SyncTasksAsync(Collection(), null, Ct));

        AssertNoSecrets(ex);
        Assert.DoesNotContain("atlassian", ex.Message + ex.Diagnostics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.atlassian.net", true)]
    [InlineData("https://example.atlassian.net/jira/software?x=1", true)]
    [InlineData("http://example.atlassian.net", false)]
    [InlineData("https://user:pw@example.atlassian.net", false)]
    [InlineData("example.atlassian.net", false)]
    [InlineData("ftp://example.atlassian.net", false)]
    [InlineData("https://127.0.0.1", false)]
    public void Only_https_site_addresses_are_accepted(string site, bool valid)
    {
        var errors = new JiraFactory(new FakeHandler()).ValidateValues(new Dictionary<string, string> { [JiraFactory.SiteField] = site });

        Assert.Equal(valid, !errors.Any());
    }

    [Fact]
    public void The_site_is_reduced_to_its_origin()
    {
        Assert.Equal(new Uri("https://example.atlassian.net/"), JiraClient.NormalizeSite(" https://example.atlassian.net/jira/path?x=1#y "));
    }

    [Fact]
    public void An_email_with_a_colon_or_space_is_refused()
    {
        var factory = new JiraFactory(new FakeHandler());

        Assert.NotEmpty(factory.ValidateValues(new Dictionary<string, string> { [JiraFactory.EmailField] = "a:b@example.com" }));
        Assert.NotEmpty(factory.ValidateValues(new Dictionary<string, string> { [JiraFactory.EmailField] = "a b@example.com" }));
        Assert.Empty(factory.ValidateValues(new Dictionary<string, string> { [JiraFactory.EmailField] = Email }));
    }

    [Fact]
    public void The_token_is_a_password_field_and_the_site_and_email_are_required()
    {
        var fields = new JiraFactory(new FakeHandler()).ConfigSchema.Fields.ToDictionary(f => f.Key);

        Assert.True(fields[JiraFactory.TokenField].IsSecretField);
        Assert.False(fields[JiraFactory.SiteField].IsSecretField);
        Assert.True(fields[JiraFactory.SiteField].Required);
        Assert.True(fields[JiraFactory.EmailField].Required);
        Assert.Equal("jira", new JiraFactory(new FakeHandler()).Kind);
    }

    [Fact]
    public void Creating_a_source_with_an_insecure_site_throws()
    {
        var config = new SourceConfig(new Dictionary<string, string> { [JiraFactory.SiteField] = "http://example.atlassian.net" });

        Assert.Throws<ArgumentException>(() => new JiraFactory(new FakeHandler()).Create("jira-1", "Jira", config, new InMemorySecretStore()));
    }

    private static void AssertNoSecrets(SourceException ex)
    {
        var text = ex.Message + ex.Diagnostics + ex;
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Secret}")), text, StringComparison.Ordinal);
    }
}
