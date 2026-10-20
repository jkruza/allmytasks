using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.ICal;

/// <summary>What a conditional fetch returned.</summary>
/// <param name="NotModified">The server answered 304; nothing else is set.</param>
/// <param name="Text">The decoded body.</param>
/// <param name="ETag">The <c>ETag</c> header, if any.</param>
/// <param name="LastModified">The <c>Last-Modified</c> header, if any.</param>
/// <param name="Hash">A hash of the body bytes, to skip no-op writes when the server sends no validators.</param>
public sealed record FeedResponse(bool NotModified, string Text, string? ETag, string? LastModified, string Hash);

/// <summary>Validators from the previous fetch, sent back as <c>If-None-Match</c> / <c>If-Modified-Since</c>.</summary>
public sealed record FeedValidators(string? ETag, string? LastModified);

/// <summary>
/// Fetches an <c>.ics</c> feed. Redirects are followed by hand so that one is never followed from https to http and
/// the request never leaves the redirect chain's own scheme rules; the body is size-capped and the request times out.
/// Exceptions are <see cref="SourceException"/> subtypes with messages that never include the URL.
/// </summary>
public sealed class IcalFeedClient(HttpMessageHandler handler, long maxBytes = IcalFeedClient.DefaultMaxBytes, TimeSpan? timeout = null) : IDisposable
{
    public const long DefaultMaxBytes = 20L * 1024 * 1024;
    internal const int MaxRedirects = 5;
    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public void Dispose() => _http.Dispose();

    /// <summary>Creates the handler the app uses: no automatic redirects (handled here), transparent decompression.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    public async Task<FeedResponse> FetchAsync(Uri url, FeedValidators? validators, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            return await FetchCoreAsync(url, validators, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException("The feed took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            // The framework message can contain the host and sometimes more; only the kind is kept.
            throw new SourceUnavailableException($"The feed could not be reached ({ex.HttpRequestError}).");
        }
    }

    private async Task<FeedResponse> FetchCoreAsync(Uri url, FeedValidators? validators, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/calendar, text/plain;q=0.9, */*;q=0.5");
            request.Headers.UserAgent.ParseAdd("AllMyTasks/0.1");
            if (validators?.ETag is { Length: > 0 } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            if (validators?.LastModified is { Length: > 0 } modified)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is 301 or 302 or 303 or 307 or 308)
            {
                current = NextHop(current, response.Headers.Location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FeedResponse(true, string.Empty, validators?.ETag, validators?.LastModified, string.Empty);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SourceAuthException("The server refused access to the feed.");
            }

            if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            {
                throw new SourceUnavailableException($"The feed server answered with an error (HTTP {status}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException($"The feed server answered HTTP {status}.");
            }

            return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new SourceDataException("The feed redirected too many times.");
    }

    /// <summary>Resolves a redirect, refusing to downgrade from https to http.</summary>
    internal static Uri NextHop(Uri current, Uri? location)
    {
        if (location is null)
        {
            throw new SourceDataException("The feed redirected without saying where.");
        }

        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (next.Scheme is not ("http" or "https"))
        {
            throw new SourceDataException("The feed redirected to an unsupported address.");
        }

        if (current.Scheme == "https" && next.Scheme == "http")
        {
            throw new SourceDataException("The feed redirected from https to http, which is not followed.");
        }

        return next;
    }

    private async Task<FeedResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new SourceDataException("The feed is larger than the allowed size.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new SourceDataException("The feed is larger than the allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var text = Decode(bytes, response.Content.Headers.ContentType);
        var etag = response.Headers.ETag?.ToString();
        var lastModified = response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return new FeedResponse(false, text, etag, lastModified, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Decodes with a BOM if present, else the declared charset, else UTF-8 (falling back to Latin-1 for invalid UTF-8).</summary>
    internal static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var charset = contentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Unknown charset name: fall through to UTF-8 detection.
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
