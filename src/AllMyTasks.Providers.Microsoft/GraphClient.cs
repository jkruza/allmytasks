using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>
/// Calls Microsoft Graph. Every address, including the <c>nextLink</c> and <c>deltaLink</c> values the server returns, must be
/// <c>https</c> on <c>graph.microsoft.com</c> or the request is refused before the bearer token is attached; redirects are never
/// followed; bodies are size-capped and requests time out. Exceptions are <see cref="SourceException"/> subtypes whose messages
/// never include the token, an address or any of the account's data.
/// </summary>
internal sealed class GraphClient(
    HttpMessageHandler handler,
    Func<CancellationToken, Task<string>> getToken,
    long maxBytes = GraphClient.DefaultMaxBytes,
    TimeSpan? timeout = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;
    public const string Host = "graph.microsoft.com";
    internal static readonly Uri Root = new("https://graph.microsoft.com/v1.0/");

    /// <summary>A wait asked for by the server up to this long is served inside the client; a longer one goes to the engine.</summary>
    internal static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(30);

    internal const int MaxAttempts = 3;

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the handler the app uses: no automatic redirects, transparent decompression.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Builds an address under the API root from a path that is already escaped.</summary>
    public static Uri Api(string relativePath) => new(Root, relativePath);

    /// <summary>
    /// Accepts only an <c>https</c> address on the Graph host with no credentials in it. Used for every address that came from
    /// the server or from saved state.
    /// </summary>
    public static Uri Pin(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
        {
            throw new SourceDataException("Microsoft Graph returned an address that is not on graph.microsoft.com, so it was not followed.");
        }

        return uri;
    }

    public Task<GraphPage<T>> GetPageAsync<T>(Uri url, JsonTypeInfo<GraphPage<T>> typeInfo, string? prefer, CancellationToken cancellationToken) =>
        GetAsync(url, typeInfo, prefer, cancellationToken);

    /// <summary>Reads one Graph object (not a collection page), with the same retry and size rules as a page.</summary>
    public async Task<T> GetAsync<T>(Uri url, JsonTypeInfo<T> typeInfo, string? prefer, CancellationToken cancellationToken)
    {
        Pin(url.AbsoluteUri);
        for (var attempt = 1; ; attempt++)
        {
            var token = await getToken(cancellationToken).ConfigureAwait(false);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            try
            {
                return await SendAsync(url, typeInfo, prefer, token, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (SourceUnavailableException ex) when (attempt < MaxAttempts && ex.RetryAfter is { } wait && wait <= MaxInlineWait)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SourceUnavailableException("Microsoft Graph took too long to respond.")
                {
                    Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
                };
            }
            catch (HttpRequestException ex)
            {
                // The framework message can hold the address; only the kind is kept.
                throw new SourceUnavailableException($"Microsoft Graph could not be reached ({ex.HttpRequestError}).")
                {
                    Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
                };
            }
        }
    }

    private async Task<T> SendAsync<T>(Uri url, JsonTypeInfo<T> typeInfo, string? prefer, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");
        if (prefer is not null)
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false), typeInfo);
        }

        var code = await ErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
        var diagnostics = code is null ? $"HTTP {status}" : $"HTTP {status}; {code}";
        throw status switch
        {
            401 or 403 => new SourceAuthException("Microsoft refused access to this account's data. Edit the source to sign in again.") { Diagnostics = diagnostics },
            404 when code is "MailboxNotEnabledForRESTAPI" or "MailboxNotSupportedForRESTAPI" or "MailboxNotFound" =>
                new SourceException("This Microsoft account has no calendar or To Do mailbox that Graph can read.") { Diagnostics = diagnostics },
            404 => new GraphNotFoundException { Diagnostics = diagnostics },
            410 => new SyncStateExpiredException("Microsoft Graph no longer accepts the saved sync position.") { Diagnostics = diagnostics },
            429 or 503 or 504 => new SourceUnavailableException($"Microsoft Graph asked the app to slow down or is busy (HTTP {status}). It will try again later.")
            {
                RetryAfter = RetryAfter(response),
                Diagnostics = diagnostics,
            },
            >= 500 or 408 => new SourceUnavailableException($"Microsoft Graph answered with an error (HTTP {status}).") { Diagnostics = diagnostics },
            >= 300 and < 400 => new SourceDataException("Microsoft Graph redirected the request. Redirects are not followed, so the token is never sent elsewhere.") { Diagnostics = diagnostics },
            _ => new SourceException($"Microsoft Graph answered HTTP {status}.") { Diagnostics = diagnostics },
        };
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

    private static T Parse<T>(byte[] body, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo) ?? throw new JsonException("Empty reply.");
        }
        catch (JsonException ex)
        {
            // The message names the position in the body, not its content.
            throw new SourceDataException("The Microsoft Graph reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };
        }
    }

    private async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > 65536)
            {
                return null;
            }

            var body = await ReadAsync(response, cancellationToken, 65536).ConfigureAwait(false);
            var code = JsonSerializer.Deserialize(body, MicrosoftJson.Default.GraphErrorEnvelope)?.Error?.Code;

            // Only a plain error code goes into diagnostics; anything else could carry data.
            return code is { Length: > 0 and <= 64 } && code.All(char.IsAsciiLetterOrDigit) ? code : null;
        }
        catch (Exception ex) when (ex is JsonException or SourceDataException or IOException or HttpRequestException)
        {
            return null;
        }
    }

    private async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken, long? limit = null)
    {
        var cap = limit ?? maxBytes;
        if (response.Content.Headers.ContentLength is { } length && length > cap)
        {
            throw TooLarge(cap);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw TooLarge(cap);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static SourceDataException TooLarge(long cap) =>
        new("The Microsoft Graph reply is larger than the allowed size.") { Diagnostics = $"Limit: {cap.ToString(CultureInfo.InvariantCulture)} bytes" };
}

/// <summary>A calendar or list that Graph says does not exist (or no longer exists). The source keeps its cache and warns.</summary>
internal sealed class GraphNotFoundException : SourceException
{
    public GraphNotFoundException()
        : base("Microsoft Graph could not find this calendar or list.")
    {
    }
}
