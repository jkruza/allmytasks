using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.GitHub;

/// <summary>
/// Calls the GitHub REST API on github.com. The host is fixed in code, the token is only ever sent to it as a bearer
/// token, redirects are never followed (so the token cannot travel to another host), bodies are size-capped and
/// requests time out. Replies are remembered with their ETag and asked for again with <c>If-None-Match</c>, because a
/// 304 does not count against the rate limit. Exceptions are <see cref="SourceException"/> subtypes whose messages
/// never include the token, the address or the body.
/// </summary>
internal sealed class GitHubClient(HttpMessageHandler handler, Func<CancellationToken, Task<string?>> getToken, long maxBytes = GitHubClient.DefaultMaxBytes, TimeSpan? timeout = null) : IDisposable
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;
    internal const int PageSize = 100;

    /// <summary>The search API serves at most 1000 results, which is 10 pages of 100.</summary>
    internal const int MaxSearchPages = 10;

    internal const int MaxIssuePages = 30;
    internal const string ReviewSearch = "is:pr is:open review-requested:@me archived:false";
    internal static readonly Uri BaseUrl = new("https://api.github.com/");

    internal const int MaxCachedPages = 200;

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, CachedPage> _pages = new();

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the handler the app uses: no automatic redirects, transparent decompression.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Open issues and pull requests assigned to the token's user.</summary>
    public async Task<IReadOnlyList<IssueDto>> GetAssignedAsync(CancellationToken cancellationToken)
    {
        var items = new List<IssueDto>();
        for (var page = 1; page <= MaxIssuePages; page++)
        {
            var body = await GetAsync($"issues?filter=assigned&state=open&per_page={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            var batch = ParseIssues(body);
            items.AddRange(batch);
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        return items;
    }

    /// <summary>Open pull requests where the token's user is asked to review.</summary>
    public async Task<IReadOnlyList<IssueDto>> GetReviewRequestsAsync(CancellationToken cancellationToken)
    {
        var items = new List<IssueDto>();
        var query = Uri.EscapeDataString(ReviewSearch);
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            var body = await GetAsync($"search/issues?q={query}&per_page={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            var search = ParseSearch(body);
            if (search.IncompleteResults)
            {
                // Acting on a partial list would make the sync drop real items.
                throw new SourceUnavailableException("GitHub's search gave an incomplete answer. It will try again later.") { Diagnostics = "incomplete_results: true" };
            }

            var batch = search.Items ?? [];
            items.AddRange(batch);
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        return items;
    }

    internal static List<IssueDto> ParseIssues(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, GitHubJson.Default.ListIssueDto) ?? [];
        }
        catch (JsonException ex)
        {
            throw Unreadable(ex, body);
        }
    }

    internal static SearchDto ParseSearch(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, GitHubJson.Default.SearchDto) ?? new SearchDto();
        }
        catch (JsonException ex)
        {
            throw Unreadable(ex, body);
        }
    }

    /// <summary>A token that can go into a header: printable ASCII, no spaces. Anything else is refused before it is sent.</summary>
    internal static bool IsUsableToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.All(c => c is > ' ' and < '\u007f');

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            && epoch > 0 && epoch < 32_503_680_000)
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    private static SourceDataException Unreadable(JsonException ex, byte[] body) =>
        // The message names the position in the body, not its content.
        new("The GitHub reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        // A 403 is a rate limit when no requests are left or GitHub says how long to wait; otherwise it is a refusal.
        return response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"));
    }

    private async Task<byte[]> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var token = await getToken(cancellationToken).ConfigureAwait(false);
        if (token is null || !IsUsableToken(token))
        {
            throw new SourceAuthException("No GitHub token is stored for this account. Edit the source to enter it again.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await SendAsync(token, relativeUrl, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("GitHub took too long to respond.")
            {
                Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
            };
        }
        catch (HttpRequestException ex)
        {
            // The framework message can hold the address; only the kind is kept.
            throw new SourceUnavailableException($"GitHub could not be reached ({ex.HttpRequestError}).")
            {
                Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
            };
        }
    }

    private async Task<byte[]> SendAsync(string token, string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl, relativeUrl));
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");
        _pages.TryGetValue(relativeUrl, out var cached);
        if (cached is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Body;
        }

        if (IsRateLimited(response))
        {
            var wait = RetryAfter(response);
            throw new SourceUnavailableException("GitHub asked the app to slow down (rate limit). It will try again later.")
            {
                RetryAfter = wait,
                Diagnostics = $"HTTP {status}{(wait is { } w ? $"; wait {w.TotalSeconds:0} seconds" : string.Empty)}",
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SourceAuthException("GitHub rejected the token. Edit the source to enter a new one.") { Diagnostics = $"HTTP {status}" };
        }

        if (status is >= 300 and < 400)
        {
            throw new SourceDataException("GitHub redirected the request. Redirects are not followed, so the token is never sent elsewhere.") { Diagnostics = $"HTTP {status}" };
        }

        if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new SourceUnavailableException($"GitHub answered with an error (HTTP {status}).") { Diagnostics = $"HTTP {status}" };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceException($"GitHub answered HTTP {status}.") { Diagnostics = $"HTTP {status}" };
        }

        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Headers.ETag?.ToString() is { Length: > 0 } etag)
        {
            if (_pages.Count >= MaxCachedPages)
            {
                _pages.Clear();
            }

            _pages[relativeUrl] = new CachedPage(etag, body);
        }

        return body;
    }

    private async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw TooLarge();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private SourceDataException TooLarge() =>
        new("The GitHub reply is larger than the allowed size.") { Diagnostics = $"Limit: {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes" };

    private sealed record CachedPage(string ETag, byte[] Body);
}
