using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AllMyTasks.Core.Updates;

/// <summary>
/// Reads the public releases of this repository from the GitHub API with one anonymous request: no token, no cookies,
/// no redirects, a generic <c>User-Agent</c> and nothing that identifies the user, machine or installation.
/// Drafts are ignored, and only release pages inside this repository are accepted as links.
/// </summary>
public sealed class GitHubReleaseChecker : IReleaseChecker, IDisposable
{
    public const string Repository = "jkruza/allmytasks";
    internal const long MaxBytes = 1024 * 1024;
    internal const string ReleasePrefix = "https://github.com/" + Repository + "/";
    private static readonly Uri Endpoint = new($"https://api.github.com/repos/{Repository}/releases?per_page=20");

    private readonly HttpClient _http;

    /// <summary>Uses the default handler, or <paramref name="handler"/> in tests.</summary>
    public GitHubReleaseChecker(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public void Dispose() => _http.Dispose();

    public async Task<ReleaseCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AllMyTasks", null));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ReleaseCheckResult.NotModified;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return ReleaseCheckResult.Failed;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBytes)
                    {
                        return ReleaseCheckResult.Failed;
                    }

                    buffer.Write(chunk, 0, read);
                }

                var releases = Parse(buffer.ToArray());
                return releases is null
                    ? ReleaseCheckResult.Failed
                    : new ReleaseCheckResult(ReleaseCheckStatus.Ok, releases, response.Headers.ETag?.ToString());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return ReleaseCheckResult.Failed;
        }
    }

    internal static List<ReleaseInfo>? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<ReleaseInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Bool(item, "draft"))
            {
                continue;
            }

            var tag = Text(item, "tag_name");
            var url = Text(item, "html_url");
            if (ReleaseVersion.TryParse(tag) is null || url is null || !url.StartsWith(ReleasePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new ReleaseInfo(tag!, url, Bool(item, "prerelease")));
        }

        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
