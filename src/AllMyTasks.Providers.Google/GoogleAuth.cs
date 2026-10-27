using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Providers.Google;

/// <summary>Opens the sign-in address in the user's browser.</summary>
internal delegate Task BrowserLauncher(Uri address, CancellationToken cancellationToken);

/// <summary>
/// Signs a Google account in and keeps its access token fresh. OAuth 2.0 authorization code flow with PKCE in the system browser
/// and a loopback redirect (RFC 8252). The client is a "Desktop app" client; Google documents its secret as not confidential, but
/// it is still supplied through configuration, never committed. Only the refresh token (and the non-secret account id used to
/// notice a different account) is stored, in the secret store; the short-lived access token stays in memory. Messages never
/// include tokens, codes or the server's error text.
/// </summary>
internal sealed class GoogleAuth : IDisposable
{
    public const string RefreshTokenField = "refresh-token";
    public const string AccountField = "account-sub";
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events.readonly";
    public const string TasksScope = "https://www.googleapis.com/auth/tasks.readonly";

    internal static readonly Uri AuthorizeUrl = new("https://accounts.google.com/o/oauth2/v2/auth");
    internal static readonly Uri TokenUrl = new("https://oauth2.googleapis.com/token");
    internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _sourceId;
    private readonly ISecretStore _secrets;
    private readonly string? _clientId;
    private readonly Func<CancellationToken, Task<string?>> _clientSecret;
    private readonly string[] _scopes;
    private readonly BrowserLauncher _openBrowser;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;
    private bool _signInTried;

    public GoogleAuth(
        HttpMessageHandler handler,
        string sourceId,
        ISecretStore secrets,
        string? clientId,
        Func<CancellationToken, Task<string?>> clientSecret,
        IEnumerable<string> scopes,
        BrowserLauncher openBrowser,
        TimeProvider? time = null)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _sourceId = sourceId;
        _secrets = secrets;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scopes = [.. scopes.SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
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

    /// <summary>Forgets the cached access token, for example after the API answered 401.</summary>
    public void Invalidate() => _accessToken = null;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _time.GetUtcNow() + Skew < _expires)
            {
                return _accessToken;
            }

            if (string.IsNullOrWhiteSpace(_clientId))
            {
                throw new SourceAuthException(
                    "No Google OAuth client id is set. Create a Desktop-app OAuth client as described in the docs and enter its id in this source.");
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
                throw new SourceAuthException("Sign in to Google again: edit this source and save it.");
            }

            _signInTried = true;
            return await SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> SignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var query = string.Join('&', new Dictionary<string, string>
            {
                ["client_id"] = _clientId!,
                ["response_type"] = "code",
                ["redirect_uri"] = redirect,
                ["scope"] = string.Join(' ', _scopes.Prepend("openid")),
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["access_type"] = "offline",
                ["prompt"] = "consent select_account",
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
                throw new SourceAuthException("Sign-in to Google was not completed in time. Edit this source and save it to try again.");
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
        var fields = new List<KeyValuePair<string, string>> { KeyValuePair.Create("client_id", _clientId!) };
        if (await _clientSecret(cancellationToken).ConfigureAwait(false) is { Length: > 0 } secret)
        {
            fields.Add(KeyValuePair.Create("client_secret", secret.Trim()));
        }

        fields.AddRange(grant);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(fields) };
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
            throw new SourceUnavailableException($"Google sign-in could not be reached ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("Google sign-in took too long to respond.");
        }

        if (reply?.AccessToken is { Length: > 0 } access)
        {
            if (!isRefresh)
            {
                await CheckSignInAsync(reply, cancellationToken).ConfigureAwait(false);
            }

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
        if (isRefresh && error is "invalid_grant")
        {
            // The saved sign-in no longer works (revoked, password changed, unused for six months, or the 7-day limit of an app
            // still in testing).
            await _secrets.DeleteAsync(_sourceId, RefreshTokenField, CancellationToken.None).ConfigureAwait(false);
            throw new SourceAuthException("Google sign-in has expired or was revoked. Edit this source and save it to sign in again.") { Diagnostics = $"Token endpoint: {error}" };
        }

        if (error is "invalid_client" or "unauthorized_client" or "invalid_request" or "invalid_grant" or "access_denied" or "unsupported_grant_type" or "redirect_uri_mismatch" or "admin_policy_enforced")
        {
            throw new SourceAuthException("Google did not accept the sign-in. Check the OAuth client id and secret of this source and that the client is a Desktop app.") { Diagnostics = $"Token endpoint: {error}" };
        }

        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
        {
            throw new SourceUnavailableException($"Google sign-in is busy (HTTP {(int)status}).");
        }

        throw new SourceException($"Google sign-in answered HTTP {(int)status}.") { Diagnostics = error is { Length: <= 64 } ? $"Token endpoint: {error}" : null };
    }

    /// <summary>After a fresh sign-in: every permission must be granted, and it must be the same account as before.</summary>
    private async Task CheckSignInAsync(TokenResponseDto reply, CancellationToken cancellationToken)
    {
        var granted = (reply.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var missing = _scopes.Where(s => !granted.Contains(s, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            throw new SourceAuthException(
                "Google sign-in did not grant every permission this source needs. Edit the source, sign in again and leave all the boxes ticked.")
            {
                Diagnostics = $"Missing scopes: {string.Join(' ', missing.Select(m => m[(m.LastIndexOf('/') + 1)..]))}",
            };
        }

        var sub = SubjectOf(reply.IdToken);
        if (sub is null)
        {
            return;
        }

        var known = await _secrets.GetAsync(_sourceId, AccountField, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(known) && known != sub)
        {
            throw new SourceAuthException("That is a different Google account from the one this source was set up with, so it was not used. Sign in with the original account or add a new source.");
        }

        await _secrets.SetAsync(_sourceId, AccountField, sub, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>sub</c> claim of an ID token. The token came straight from Google's token endpoint over TLS, so it is read, not verified.</summary>
    internal static string? SubjectOf(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            return JsonSerializer.Deserialize(Convert.FromBase64String(payload), GoogleJson.Default.IdTokenClaims)?.Sub is { Length: > 0 and <= 256 } sub ? sub : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static TokenResponseDto? ParseToken(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, GoogleJson.Default.TokenResponseDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
