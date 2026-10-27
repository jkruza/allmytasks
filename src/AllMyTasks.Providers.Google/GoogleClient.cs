using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Google;

/// <summary>
/// Calls the Google Calendar and Tasks REST APIs. Every address must be <c>https</c> on one of the two API hosts or the request is
/// refused before the bearer token is attached; redirects are never followed; bodies are size-capped and requests time out.
/// Exceptions are <see cref="SourceException"/> subtypes whose messages never include the token, an address or any of the
/// account's data.
/// </summary>
internal sealed class GoogleClient(
    HttpMessageHandler handler,
    Func<CancellationToken, Task<string>> getToken,
    Action invalidateToken,
    long maxBytes = GoogleClient.DefaultMaxBytes,
    TimeSpan? timeout = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    public const long DefaultMaxBytes = 32L * 1024 * 1024;
    public const string CalendarHost = "www.googleapis.com";
    public const string TasksHost = "tasks.googleapis.com";
    internal static readonly Uri CalendarRoot = new("https://www.googleapis.com/calendar/v3/");
    internal static readonly Uri TasksRoot = new("https://tasks.googleapis.com/tasks/v1/");

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

    /// <summary>Builds an address under <paramref name="root"/> from a path that is already escaped, plus query parameters (null values are left out).</summary>
    public static Uri Api(Uri root, string relativePath, params (string Name, string? Value)[] query)
    {
        var pairs = query.Where(q => q.Value is not null).Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}").ToList();
        return new(root, pairs.Count == 0 ? relativePath : $"{relativePath}?{string.Join('&', pairs)}");
    }

    private static void RequireApiHost(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps
            || !(string.Equals(url.Host, CalendarHost, StringComparison.OrdinalIgnoreCase) || string.Equals(url.Host, TasksHost, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(url.UserInfo)
            || !url.IsDefaultPort)
        {
            throw new SourceDataException("A Google API address was not on googleapis.com, so it was not used.");
        }
    }

    public async Task<T> GetAsync<T>(Uri url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        RequireApiHost(url);
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await getToken(cancellationToken).ConfigureAwait(false);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            try
            {
                return await SendAsync(url, typeInfo, token, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (SourceUnavailableException ex) when (attempt < MaxAttempts && ex.RetryAfter is { } wait && wait <= MaxInlineWait)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedException) when (!refreshed)
            {
                // The access token may have been revoked or may have expired early: get a new one once.
                refreshed = true;
                invalidateToken();
            }
            catch (UnauthorizedException ex)
            {
                throw new SourceAuthException("Google refused access to this account's data. Edit the source to sign in again.") { Diagnostics = ex.Diagnostics };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SourceUnavailableException("Google took too long to respond.")
                {
                    Diagnostics = $"No complete response within {_timeout.TotalSeconds:0} seconds.",
                };
            }
            catch (HttpRequestException ex)
            {
                // The framework message can hold the address; only the kind is kept.
                throw new SourceUnavailableException($"Google could not be reached ({ex.HttpRequestError}).")
                {
                    Diagnostics = $"HttpRequestError: {ex.HttpRequestError}",
                };
            }
        }
    }

    private async Task<T> SendAsync<T>(Uri url, JsonTypeInfo<T> typeInfo, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1 (gzip)");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false), typeInfo);
        }

        var reason = await ErrorReasonAsync(response, cancellationToken).ConfigureAwait(false);
        var diagnostics = reason is null ? $"HTTP {status}" : $"HTTP {status}; {reason}";
        throw status switch
        {
            401 => new UnauthorizedException { Diagnostics = diagnostics },
            403 when reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "dailyLimitExceeded" =>
                new SourceUnavailableException("Google asked the app to slow down. It will try again later.") { RetryAfter = RetryAfter(response), Diagnostics = diagnostics },
            403 when reason is "accessNotConfigured" =>
                new SourceException("The Calendar or Tasks API is not enabled for the Google Cloud project of this app's OAuth client.") { Diagnostics = diagnostics },
            403 => new SourceAuthException("Google refused access to this account's data. Edit the source to sign in again and tick every permission.") { Diagnostics = diagnostics },
            404 => new GoogleNotFoundException { Diagnostics = diagnostics },
            410 => new SyncStateExpiredException("Google no longer accepts the saved sync position.") { Diagnostics = diagnostics },
            429 or 503 => new SourceUnavailableException($"Google asked the app to slow down or is busy (HTTP {status}). It will try again later.")
            {
                RetryAfter = RetryAfter(response),
                Diagnostics = diagnostics,
            },
            >= 500 or 408 => new SourceUnavailableException($"Google answered with an error (HTTP {status}).") { Diagnostics = diagnostics },
            >= 300 and < 400 => new SourceDataException("Google redirected the request. Redirects are not followed, so the token is never sent elsewhere.") { Diagnostics = diagnostics },
            _ => new SourceException($"Google answered HTTP {status}.") { Diagnostics = diagnostics },
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
            throw new SourceDataException("The Google reply could not be read.") { Diagnostics = $"Invalid JSON ({ex.GetType().Name}); {body.Length} bytes" };
        }
    }

    private async Task<string?> ErrorReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > 65536)
            {
                return null;
            }

            var body = await ReadAsync(response, cancellationToken, 65536).ConfigureAwait(false);
            var reason = JsonSerializer.Deserialize(body, GoogleJson.Default.GoogleErrorEnvelope)?.Error?.Errors?.FirstOrDefault()?.Reason;

            // Only a plain reason code goes into diagnostics; anything else could carry data.
            return reason is { Length: > 0 and <= 64 } && reason.All(char.IsAsciiLetterOrDigit) ? reason : null;
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
        new("The Google reply is larger than the allowed size.") { Diagnostics = $"Limit: {cap.ToString(CultureInfo.InvariantCulture)} bytes" };

    private sealed class UnauthorizedException : SourceException;
}

/// <summary>A calendar or list that Google says does not exist (or no longer exists). The source keeps its cache and warns.</summary>
internal sealed class GoogleNotFoundException : SourceException
{
    public GoogleNotFoundException()
        : base("Google could not find this calendar or list.")
    {
    }
}
