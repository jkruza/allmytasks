using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Providers.Microsoft;

/// <summary>Opens the sign-in address in the user's browser.</summary>
internal delegate Task BrowserLauncher(Uri address, CancellationToken cancellationToken);

/// <summary>
/// Signs a personal Microsoft account in and keeps its access token fresh. OAuth 2.0 authorization code flow with PKCE in the system
/// browser and a loopback redirect, against the <c>consumers</c> authority; the app is a public client, so no secret exists.
/// Only the refresh token is stored, in the secret store; the short-lived access token stays in memory. Messages never include
/// tokens, codes or the server's error text.
/// </summary>
internal sealed class MicrosoftAuth : IDisposable
{
    public const string RefreshTokenField = "refresh-token";
    internal static readonly Uri AuthorizeUrl = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize");
    internal static readonly Uri TokenUrl = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");
    internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string _sourceId;
    private readonly ISecretStore _secrets;
    private readonly string? _clientId;
    private readonly string _scope;
    private readonly BrowserLauncher _openBrowser;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;
    private bool _signInTried;

    public MicrosoftAuth(
        HttpMessageHandler handler,
        string sourceId,
        ISecretStore secrets,
        string? clientId,
        IEnumerable<string> scopes,
        BrowserLauncher openBrowser,
        TimeProvider? time = null)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _sourceId = sourceId;
        _secrets = secrets;
        _clientId = clientId;
        _scope = string.Join(' ', scopes.Append("offline_access"));
        _openBrowser = openBrowser;
        _time = time ?? TimeProvider.System;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>Opens the address with the operating system's default handler. Only <c>https</c> addresses are opened.</summary>
    public static Task OpenWithShellAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new SourceException("The sign-in address is not https and was not opened.");
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new SourceException($"No web browser could be opened for sign-in ({ex.GetType().Name}).");
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _time.GetUtcNow() + Skew < _expires)
            {
                return _accessToken;
            }

            if (!IsClientId(_clientId))
            {
                throw new SourceAuthException(
                    "No Microsoft application (client) id is set. Register a free app as described in the docs and enter its id in this source.");
            }

            var refresh = await _secrets.GetAsync(_sourceId, RefreshTokenField, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(refresh))
            {
                return await RedeemAsync(
                    [KeyValuePair.Create("grant_type", "refresh_token"), KeyValuePair.Create("refresh_token", refresh)],
                    isRefresh: true,
                    cancellationToken).ConfigureAwait(false);
            }

            // Opening a browser from a background sync is only acceptable once: the first sync after the source was added or
            // edited. Afterwards the user is told to edit the source, which creates a fresh instance.
            if (_signInTried)
            {
                throw new SourceAuthException("Sign in to Microsoft again: edit this source and save it.");
            }

            _signInTried = true;
            return await SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool IsClientId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;

    private async Task<string> SignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var query = string.Join('&', new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["response_type"] = "code",
                ["redirect_uri"] = redirect,
                ["response_mode"] = "query",
                ["scope"] = _scope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["prompt"] = "select_account",
            }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SignInTimeout);
            string code;
            try
            {
                // Listening starts before the browser opens, so a fast redirect cannot be missed.
                var receiving = LoopbackReceiver.WaitForCodeAsync(listener, state, timeout.Token);
                await _openBrowser(new Uri($"{AuthorizeUrl.AbsoluteUri}?{query}"), timeout.Token).ConfigureAwait(false);
                code = await receiving.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SourceAuthException("Sign-in to Microsoft was not completed in time. Edit this source and save it to try again.");
            }

            return await RedeemAsync(
                [
                    KeyValuePair.Create("grant_type", "authorization_code"),
                    KeyValuePair.Create("code", code),
                    KeyValuePair.Create("redirect_uri", redirect),
                    KeyValuePair.Create("code_verifier", verifier),
                ],
                isRefresh: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string> RedeemAsync(KeyValuePair<string, string>[] grant, bool isRefresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(
            [
                KeyValuePair.Create("client_id", _clientId!),
                KeyValuePair.Create("scope", _scope),
                .. grant,
            ]),
        };
        request.Headers.Accept.ParseAdd("application/json");

        TokenResponseDto? reply;
        HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            reply = body.Length is > 0 and < 1_000_000 ? ParseToken(body) : null;
        }
        catch (HttpRequestException ex)
        {
            throw new SourceUnavailableException($"Microsoft sign-in could not be reached ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("Microsoft sign-in took too long to respond.");
        }

        if (reply?.AccessToken is { Length: > 0 } access)
        {
            _accessToken = access;
            _expires = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(reply.ExpiresIn, 60));
            if (reply.RefreshToken is { Length: > 0 } newRefresh)
            {
                await _secrets.SetAsync(_sourceId, RefreshTokenField, newRefresh, cancellationToken).ConfigureAwait(false);
            }

            return access;
        }

        _accessToken = null;
        var error = reply?.Error;
        if (error is "invalid_grant" or "interaction_required" or "consent_required" or "invalid_scope" && isRefresh)
        {
            // The saved sign-in no longer works (password changed, access revoked, or the permissions asked for changed).
            await _secrets.DeleteAsync(_sourceId, RefreshTokenField, CancellationToken.None).ConfigureAwait(false);
            throw new SourceAuthException("Microsoft sign-in has expired or was revoked. Edit this source and save it to sign in again.") { Diagnostics = $"Token endpoint: {error}" };
        }

        if (error is "invalid_client" or "unauthorized_client" or "invalid_request" or "invalid_grant" or "access_denied" or "unsupported_grant_type")
        {
            throw new SourceAuthException("Microsoft did not accept the sign-in. Check the application (client) id of this source and that the app accepts personal accounts.") { Diagnostics = $"Token endpoint: {error}" };
        }

        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
        {
            throw new SourceUnavailableException($"Microsoft sign-in is busy (HTTP {(int)status}).");
        }

        throw new SourceException($"Microsoft sign-in answered HTTP {(int)status}.") { Diagnostics = error is { Length: <= 64 } ? $"Token endpoint: {error}" : null };
    }

    private static TokenResponseDto? ParseToken(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, MicrosoftJson.Default.TokenResponseDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Receives the browser's redirect to <c>http://localhost:{port}/</c> on a loopback listener.</summary>
internal static class LoopbackReceiver
{
    internal const string Page = "<!doctype html><meta charset=\"utf-8\"><title>All My Tasks</title>"
        + "<body style=\"font-family:sans-serif;margin:3em\"><h2>You can close this tab</h2><p>Return to All My Tasks.</p></body>";

    public static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var query = await ReadQueryAsync(client, cancellationToken).ConfigureAwait(false);

            // Requests without a result (a favicon, a probe) are answered and ignored; so is anything with the wrong state.
            if (query is null || query.GetValueOrDefault("state") != expectedState)
            {
                await RespondAsync(client, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(client, "200 OK", Page, cancellationToken).ConfigureAwait(false);
            if (query.GetValueOrDefault("code") is { Length: > 0 } code)
            {
                return code;
            }

            throw new SourceAuthException(query.ContainsKey("error")
                ? "Microsoft sign-in was cancelled or declined. Edit this source and save it to try again."
                : "Microsoft sign-in did not return a result.");
        }
    }

    private static async Task<Dictionary<string, string>?> ReadQueryAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stream = client.GetStream();
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), readTimeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        var q = line.Length > 1 ? line[1].IndexOf('?') : -1;
        if (line.Length < 2 || line[0] != "GET" || q < 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var pair in line[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }

    private static async Task RespondAsync(TcpClient client, string status, string body, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{body}");
            await client.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away; the result has already been read.
        }
    }
}
