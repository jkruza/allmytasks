using System.Globalization;
using System.Net;
using System.Text.Json;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Todoist;

/// <summary>A parsed Sync API reply.</summary>
internal sealed record SyncReply(
    string SyncToken,
    bool FullSync,
    IReadOnlyList<ProjectDto> Projects,
    IReadOnlyList<ItemDto> Items,
    IReadOnlyDictionary<string, CommandStatus>? CommandStatus = null);

/// <summary>What Todoist said about one command: ok, or an error with a code and a short text.</summary>
internal sealed record CommandStatus(bool IsOk, int? ErrorCode, string? Error);

/// <summary>One command of the Sync API. <paramref name="Uuid"/> makes a retry of the same command harmless.</summary>
internal sealed record TodoistCommand(string Type, Guid Uuid, string ItemId);

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
    public Task<SyncReply> SyncAsync(string syncToken, string[] resourceTypes, CancellationToken cancellationToken) =>
        SyncAsync(syncToken, resourceTypes, null, cancellationToken);

    /// <summary>
    /// Sends commands (completing or reopening tasks) and returns Todoist's answer per command. Only a small resource
    /// list is requested back, because the caller syncs afterwards.
    /// </summary>
    public Task<SyncReply> ExecuteAsync(IReadOnlyList<TodoistCommand> commands, CancellationToken cancellationToken) =>
        SyncAsync("*", ProjectsOnly, commands, cancellationToken);

    private async Task<SyncReply> SyncAsync(string syncToken, string[] resourceTypes, IReadOnlyList<TodoistCommand>? commands, CancellationToken cancellationToken)
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
            return await SendAsync(token, syncToken, resourceTypes, commands, timeoutSource.Token).ConfigureAwait(false);
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

    private async Task<SyncReply> SendAsync(string token, string syncToken, string[] resourceTypes, IReadOnlyList<TodoistCommand>? commands, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("sync_token", syncToken),
            KeyValuePair.Create("resource_types", JsonSerializer.Serialize(resourceTypes, TodoistJson.Default.StringArray)),
        };
        if (commands is not null)
        {
            form.Add(KeyValuePair.Create("commands", WriteCommands(commands)));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SyncUrl) { Content = new FormUrlEncodedContent(form) };
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

    internal static string WriteCommands(IReadOnlyList<TodoistCommand> commands)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var command in commands)
            {
                writer.WriteStartObject();
                writer.WriteString("type", command.Type);
                writer.WriteString("uuid", command.Uuid.ToString("D"));
                writer.WriteStartObject("args");
                writer.WriteString("id", command.ItemId);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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

        return new SyncReply(syncToken, dto.FullSync, dto.Projects ?? [], dto.Items ?? [], dto.SyncStatus is { } status ? ParseStatuses(status) : null);
    }

    private static Dictionary<string, CommandStatus> ParseStatuses(Dictionary<string, JsonElement> raw)
    {
        var result = new Dictionary<string, CommandStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uuid, value) in raw)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() == "ok")
            {
                result[uuid] = new CommandStatus(true, null, null);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                int? code = value.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
                var text = value.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                result[uuid] = new CommandStatus(false, code, text);
            }
            else
            {
                result[uuid] = new CommandStatus(false, null, null);
            }
        }

        return result;
    }
}
