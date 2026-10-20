using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.ICal.CalDav;

/// <summary>An HTTP answer reduced to what the CalDAV code needs.</summary>
internal sealed record DavReply(int Status, string Body, Uri FinalUrl);

/// <summary>
/// Sends WebDAV requests with HTTP Basic authentication. Credentials go only over https (unless the source explicitly
/// allows http), only to the configured host (a redirect to another host is refused), and never appear in errors.
/// </summary>
internal sealed class CalDavClient(HttpMessageHandler handler, string username, Func<CancellationToken, Task<string?>> getPassword, bool allowInsecure, TimeSpan? timeout = null) : IDisposable
{
    internal const long MaxResponseBytes = 64L * 1024 * 1024;
    internal const int MaxRedirects = 5;
    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    public void Dispose() => _http.Dispose();

    public Task<DavReply> PropfindAsync(Uri url, int depth, string body, CancellationToken cancellationToken) =>
        SendAsync(new HttpMethod("PROPFIND"), url, depth, body, cancellationToken);

    public Task<DavReply> ReportAsync(Uri url, string body, CancellationToken cancellationToken) =>
        SendAsync(new HttpMethod("REPORT"), url, null, body, cancellationToken);

    private async Task<DavReply> SendAsync(HttpMethod method, Uri url, int? depth, string body, CancellationToken cancellationToken)
    {
        if (url.Scheme == "http" && !allowInsecure)
        {
            throw new SourceAuthException("The server address uses http, which would send the password unprotected. Use https, or allow an insecure connection for this source.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await SendCoreAsync(method, url, depth, body, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("The server took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            throw new SourceUnavailableException($"The server could not be reached ({ex.HttpRequestError}).");
        }
    }

    private async Task<DavReply> SendCoreAsync(HttpMethod method, Uri url, int? depth, string body, CancellationToken cancellationToken)
    {
        var origin = url;
        var current = url;
        var password = await getPassword(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(method, current)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
            request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");
            if (depth is { } d)
            {
                request.Headers.TryAddWithoutValidation("Depth", d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 307 or 308)
            {
                current = NextHop(origin, current, response.Headers.Location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SourceAuthException("The server rejected the user name or password.");
            }

            if (status >= 500 && status is not (501 or 507))
            {
                throw new SourceUnavailableException($"The server answered with an error (HTTP {status}).");
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxResponseBytes)
            {
                throw new SourceDataException("The server's response is larger than the allowed size.");
            }

            var text = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            return new DavReply(status, text, current);
        }

        throw new SourceDataException("The server redirected too many times.");
    }

    /// <summary>Resolves a redirect, refusing a change of host (it would receive the credentials) or a downgrade to http.</summary>
    internal static Uri NextHop(Uri origin, Uri current, Uri? location)
    {
        if (location is null)
        {
            throw new SourceDataException("The server redirected without saying where.");
        }

        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!string.Equals(next.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceDataException("The server redirected to a different host, which is not followed because it would receive the password.");
        }

        if (current.Scheme == "https" && next.Scheme == "http")
        {
            throw new SourceDataException("The server redirected from https to http, which is not followed.");
        }

        return next;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new SourceDataException("The server's response is larger than the allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return IcalFeedClient.Decode(buffer.ToArray(), response.Content.Headers.ContentType);
    }
}
