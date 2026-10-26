using System.Globalization;
using System.Net;
using System.Text.Json;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Todoist;

/// <summary>A parsed Sync API reply.</summary>
internal sealed record SyncReply(string SyncToken, bool FullSync, IReadOnlyList<ProjectDto> Projects, IReadOnlyList<ItemDto> Items);

/// <summary>
/// Calls the Todoist Sync API. The host is fixed in code, the token is only ever sent to it as a bearer token, redirects
/// are never followed (so the token cannot travel to another host), the body is size-capped and the request times out.
/// Exceptions are <see cref="SourceException"/> subtypes whose messages never include the token, the address or the body.
/// </summary>
internal sealed class TodoistClient(HttpMessageHandler handler, Func<CancellationToken, Task<string?>> getToken, long maxBytes = TodoistClient.DefaultMaxBytes, TimeSpan? timeout = null) : IDisposable
{
    public const long DefaultMaxBytes = 32L * 1024 * 1024;
    internal static readonly Uri SyncUrl = new("https://api.todoist.com/api/v1/sync");
    internal static readonly string[] ProjectsOnly = ["projects"];
    internal static readonly string[] ProjectsAndItems = ["projects", "items"];

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

    /// <summary>Asks for the given resources changed since <paramref name="syncToken"/> (<c>*</c> for everything).</summary>
    public async Task<SyncReply> SyncAsync(string syncToken, string[] resourceTypes, CancellationToken cancellationToken)
    {
        var token = await getToken(cancellationToken).ConfigureAwait(false);
        if (token is null || !IsUsableToken(token))
        {
            throw new SourceAuthException("No Todoist token is stored for this account. Edit the source to enter it again.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await SendAsync(token, syncToken, resourceTypes, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("Todoist took too long to respond.")
            {
                Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
            };
        }
        catch (HttpRequestException ex)
        {
            // The framework message can hold the address; only the kind is kept.
            throw new SourceUnavailableException($"Todoist could not be reached ({ex.HttpRequestError}).")
            {
                Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
            };
        }
    }

    /// <summary>A token that can go into a header: printable ASCII, no spaces. Anything else is refused before it is sent.</summary>
    internal static bool IsUsableToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.All(c => c is > ' ' and < '\u007f');

    private async Task<SyncReply> SendAsync(string token, string syncToken, string[] resourceTypes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncUrl)
        {
            Content = new FormUrlEncodedContent(
            [
                KeyValuePair.Create("sync_token", syncToken),
                KeyValuePair.Create("resource_types", JsonSerializer.Serialize(resourceTypes, TodoistJson.Default.StringArray)),
            ]),
        };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SourceAuthException("Todoist rejected the token. Edit the source to enter a new one.") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfter(response);
            throw new SourceUnavailableException("Todoist asked the app to slow down (HTTP 429). It will try again later.")
            {
                Diagnostics = $"HTTP 429{(wait is { } w ? $"; Retry-After {w.TotalSeconds:0} seconds" : string.Empty)}",
            };
        }

        if (status is >= 300 and < 400)
        {
            throw new SourceDataException("Todoist redirected the request. Redirects are not followed, so the token is never sent elsewhere.") { Diagnostics = $"HTTP {status}" };
        }

        if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new SourceUnavailableException($"Todoist answered with an error (HTTP {status}).") { Diagnostics = $"HTTP {status}" };
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && syncToken != "*")
        {
            // Assumed to be how an unknown or too old sync token is refused; the engine then starts over once.
            throw new SyncStateExpiredException("Todoist no longer accepts the saved sync position.") { Diagnostics = "HTTP 400 for an incremental request" };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceException($"Todoist answered HTTP {status}.") { Diagnostics = $"HTTP {status}" };
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
        new("The Todoist reply is larger than the allowed size.") { Diagnostics = $"Limit: {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes" };

    internal static SyncReply Parse(byte[] body)
    {
        SyncResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(body, TodoistJson.Default.SyncResponseDto);
        }
        catch (JsonException ex)
        {
            // The message names the position in the body, not its content.
            throw new SourceDataException("The Todoist reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };
        }

        if (dto?.SyncToken is not { Length: > 0 } syncToken)
        {
            throw new SourceDataException("The Todoist reply had no sync position.") { Diagnostics = $"{body.Length} bytes" };
        }

        return new SyncReply(syncToken, dto.FullSync, dto.Projects ?? [], dto.Items ?? []);
    }
}
