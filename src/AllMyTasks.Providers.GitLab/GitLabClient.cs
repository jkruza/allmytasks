using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.GitLab;

/// <summary>
/// Calls the GitLab REST API (v4) of one instance with a personal access token. The token is sent only to the base address
/// the user entered, redirects are never followed (so the token cannot travel to another host), bodies are size-capped and
/// requests time out. Exceptions are <see cref="SourceException"/> subtypes whose messages never include the token, the
/// address or the body.
/// </summary>
internal sealed class GitLabClient(Uri baseUrl, HttpMessageHandler handler, Func<CancellationToken, Task<string?>> getToken, long maxBytes = GitLabClient.DefaultMaxBytes, TimeSpan? timeout = null) : IDisposable
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;
    public const string DefaultBaseUrl = "https://gitlab.com";
    internal const int PageSize = 100;

    /// <summary>A guard against a server that never stops paging: 100 pages of 100 is 10,000 items per list.</summary>
    internal const int MaxPages = 100;

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);
    private readonly Uri _api = new(baseUrl.AbsoluteUri.TrimEnd('/') + "/api/v4/");

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the handler the app uses: no automatic redirects, transparent decompression.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Reads the base address a user typed: blank means gitlab.com; otherwise an absolute https address (http only for this
    /// machine), without credentials, query or fragment. A path prefix is kept, for instances served below a path.
    /// </summary>
    internal static bool TryParseBaseUrl(string? text, out Uri baseUrl)
    {
        baseUrl = new Uri(DefaultBaseUrl);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
            || parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0
            || !(parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            return false;
        }

        baseUrl = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        return true;
    }

    /// <summary>The user name behind the token, which the reviewer query needs.</summary>
    public async Task<string> GetUsernameAsync(CancellationToken cancellationToken)
    {
        var (body, _) = await GetAsync("user", cancellationToken).ConfigureAwait(false);
        var user = Deserialize(body, GitLabJson.Default.UserDto);
        return user?.Username is { Length: > 0 } name
            ? name
            : throw new SourceDataException("GitLab did not say which user the token belongs to.") { Diagnostics = $"{body.Length} bytes" };
    }

    /// <summary>Reads every page of a list endpoint. <paramref name="query"/> is an already encoded query string without <c>?</c>.</summary>
    public async Task<IReadOnlyList<WorkItemDto>> GetListAsync(string endpoint, string query, CancellationToken cancellationToken)
    {
        var result = new List<WorkItemDto>();
        var page = "1";
        for (var count = 0; page.Length > 0; count++)
        {
            if (count >= MaxPages)
            {
                throw new SourceDataException("GitLab returned more pages than the app reads.") { Diagnostics = $"More than {MaxPages} pages of {endpoint}" };
            }

            var (body, next) = await GetAsync($"{endpoint}?{query}&per_page={PageSize}&page={Uri.EscapeDataString(page)}", cancellationToken).ConfigureAwait(false);
            result.AddRange(Deserialize(body, GitLabJson.Default.ListWorkItemDto) ?? []);
            page = next;
        }

        return result;
    }

    private async Task<(byte[] Body, string NextPage)> GetAsync(string relative, CancellationToken cancellationToken)
    {
        var token = await getToken(cancellationToken).ConfigureAwait(false);
        if (token is null || !IsUsableToken(token))
        {
            throw new SourceAuthException("No GitLab token is stored for this source. Edit the source to enter it again.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await SendAsync(token, relative, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("GitLab took too long to respond.")
            {
                Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
            };
        }
        catch (HttpRequestException ex)
        {
            // The framework message can hold the address; only the kind is kept.
            throw new SourceUnavailableException($"GitLab could not be reached ({ex.HttpRequestError}).")
            {
                Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
            };
        }
    }

    /// <summary>A token that can go into a header: printable ASCII, no spaces. Anything else is refused before it is sent.</summary>
    internal static bool IsUsableToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.All(c => c is > ' ' and < '\u007f');

    private async Task<(byte[] Body, string NextPage)> SendAsync(string token, string relative, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, relative));
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SourceAuthException("GitLab rejected the token. Check that it has not expired and has the read_api scope, then edit the source to enter it again.") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfter(response);
            throw new SourceUnavailableException("GitLab asked the app to slow down (HTTP 429). It will try again later.")
            {
                RetryAfter = wait,
                Diagnostics = $"HTTP 429{(wait is { } w ? $"; Retry-After {w.TotalSeconds:0} seconds" : string.Empty)}",
            };
        }

        if (status is >= 300 and < 400)
        {
            throw new SourceDataException("GitLab redirected the request. Redirects are not followed, so the token is never sent elsewhere. Check the address of the instance.") { Diagnostics = $"HTTP {status}" };
        }

        if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new SourceUnavailableException($"GitLab answered with an error (HTTP {status}).") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SourceDataException("GitLab did not find the API at this address. Check the address of the instance.") { Diagnostics = "HTTP 404" };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceException($"GitLab answered HTTP {status}.") { Diagnostics = $"HTTP {status}" };
        }

        var next = response.Headers.TryGetValues("x-next-page", out var values) ? values.FirstOrDefault()?.Trim() ?? string.Empty : string.Empty;
        if (next.Length > 0 && !next.All(char.IsAsciiDigit))
        {
            throw new SourceDataException("GitLab sent a page number the app does not understand.") { Diagnostics = "Invalid x-next-page header" };
        }

        return (await ReadAsync(response, cancellationToken).ConfigureAwait(false), next);
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
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
        new("The GitLab reply is larger than the allowed size.") { Diagnostics = $"Limit: {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes" };

    private static T? Deserialize<T>(byte[] body, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(body, info);
        }
        catch (JsonException ex)
        {
            // The message names the position in the body, not its content.
            throw new SourceDataException("The GitLab reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };
        }
    }
}
