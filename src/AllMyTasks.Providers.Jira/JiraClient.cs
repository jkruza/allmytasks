using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Jira;

/// <summary>
/// Calls the Jira Cloud REST API v3 search. The host is the configured site and nothing else, the credentials only
/// ever go to it as a Basic header, redirects are never followed (so they cannot travel to another host), each reply is
/// size-capped and the request times out. Exceptions are <see cref="SourceException"/> subtypes whose messages never
/// include the token, the address or the body.
/// </summary>
internal sealed class JiraClient(
    HttpMessageHandler handler,
    Uri site,
    Func<CancellationToken, Task<string?>> getEmail,
    Func<CancellationToken, Task<string?>> getToken,
    long maxBytes = JiraClient.DefaultMaxBytes,
    TimeSpan? timeout = null) : IDisposable
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;

    /// <summary>The query of version 1: open issues assigned to the signed-in user.</summary>
    internal const string Jql = "assignee = currentUser() AND statusCategory != Done ORDER BY created ASC";

    internal const string Fields = "summary,duedate,priority,labels,status,parent,project,description,updated";
    internal const int PageSize = 100;
    internal const int MaxPages = 200;

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the handler the app uses: no automatic redirects, transparent decompression.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Turns what the user typed into the site's origin, or returns null when it is not an https address. A path, query
    /// or fragment is dropped, so only the host ever receives the credentials.
    /// </summary>
    internal static Uri? NormalizeSite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : null;
    }

    /// <summary>A value that can go into a Basic header: printable ASCII without spaces (and no colon in an email).</summary>
    internal static bool IsUsableCredential(string? value, bool allowColon = true) =>
        !string.IsNullOrEmpty(value) && value.All(c => c is > ' ' and < '\u007f' && (allowColon || c != ':'));

    /// <summary>Returns every open issue assigned to the user, following the page tokens to the last page.</summary>
    public async Task<IReadOnlyList<IssueDto>> SearchAsync(CancellationToken cancellationToken)
    {
        var email = (await getEmail(cancellationToken).ConfigureAwait(false))?.Trim();
        var token = (await getToken(cancellationToken).ConfigureAwait(false))?.Trim();
        if (!IsUsableCredential(email, allowColon: false) || !IsUsableCredential(token))
        {
            throw new SourceAuthException("No usable Jira email or API token is stored for this account. Edit the source to enter them again.");
        }

        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{email}:{token}")));
        var issues = new List<IssueDto>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var reply = await GetPageAsync(authorization, pageToken, cancellationToken).ConfigureAwait(false);
            issues.AddRange(reply.Issues ?? []);
            if (reply.IsLast || string.IsNullOrEmpty(reply.NextPageToken) || !seenTokens.Add(reply.NextPageToken))
            {
                return issues;
            }

            pageToken = reply.NextPageToken;
        }

        throw new SourceDataException("Jira returned more pages than the app will read.") { Diagnostics = $"Limit: {MaxPages} pages of {PageSize}" };
    }

    private async Task<SearchResponseDto> GetPageAsync(string authorization, string? pageToken, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await SendAsync(authorization, pageToken, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("Jira took too long to respond.")
            {
                Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
            };
        }
        catch (HttpRequestException ex)
        {
            // The framework message can hold the address; only the kind is kept.
            throw new SourceUnavailableException($"Jira could not be reached ({ex.HttpRequestError}).")
            {
                Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
            };
        }
    }

    private async Task<SearchResponseDto> SendAsync(string authorization, string? pageToken, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("rest/api/3/search/jql?jql=")
            .Append(Uri.EscapeDataString(Jql))
            .Append("&fields=").Append(Uri.EscapeDataString(Fields))
            .Append("&maxResults=").Append(PageSize.ToString(CultureInfo.InvariantCulture));
        if (pageToken is not null)
        {
            query.Append("&nextPageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site, query.ToString()));
        request.Headers.Authorization = new("Basic", authorization);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SourceAuthException("Jira rejected the email or API token. Edit the source to enter them again.") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfter(response);
            throw new SourceUnavailableException("Jira asked the app to slow down (HTTP 429). It will try again later.")
            {
                RetryAfter = wait,
                Diagnostics = $"HTTP 429{(wait is { } w ? $"; Retry-After {w.TotalSeconds:0} seconds" : string.Empty)}",
            };
        }

        if (status is >= 300 and < 400)
        {
            throw new SourceDataException("Jira redirected the request. Redirects are not followed, so the credentials are never sent elsewhere.") { Diagnostics = $"HTTP {status}" };
        }

        if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new SourceUnavailableException($"Jira answered with an error (HTTP {status}).") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SourceDataException("Jira did not find the search endpoint. Check the site address; only Jira Cloud sites are supported.") { Diagnostics = "HTTP 404" };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceException($"Jira answered HTTP {status}.") { Diagnostics = $"HTTP {status}" };
        }

        return Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false));
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
        new("The Jira reply is larger than the allowed size.") { Diagnostics = $"Limit: {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes" };

    internal static SearchResponseDto Parse(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, JiraJson.Default.SearchResponseDto)
                ?? throw new SourceDataException("The Jira reply was empty.") { Diagnostics = $"{body.Length} bytes" };
        }
        catch (JsonException ex)
        {
            // The message names the position in the body, not its content.
            throw new SourceDataException("The Jira reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };
        }
    }
}
