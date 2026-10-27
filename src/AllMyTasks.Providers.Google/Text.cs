using System.Globalization;
using System.Net;
using System.Text;

namespace AllMyTasks.Providers.Google;

/// <summary>Small helpers for untrusted text and Google date-times.</summary>
internal static class Text
{
    /// <summary>Plain text from an HTML body: tags dropped, line breaks kept for block elements, entities decoded.</summary>
    public static string HtmlToPlain(string html)
    {
        var sb = new StringBuilder(html.Length);
        for (var i = 0; i < html.Length; i++)
        {
            if (html[i] != '<')
            {
                sb.Append(html[i]);
                continue;
            }

            var end = html.IndexOf('>', i + 1);
            if (end < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            var tag = html.AsSpan(i + 1, end - i - 1).TrimStart('/').ToString();
            var name = new string([.. tag.TakeWhile(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
            if (name is "br" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }

            // The content of these is never text for the reader.
            if (name is "style" or "script" && !html.AsSpan(i + 1, 1).Equals("/", StringComparison.Ordinal))
            {
                var close = html.IndexOf("</" + name, end, StringComparison.OrdinalIgnoreCase);
                end = close < 0 ? html.Length - 1 : html.IndexOf('>', close) is var gt and >= 0 ? gt : html.Length - 1;
            }

            i = end;
        }

        return WebUtility.HtmlDecode(sb.ToString()).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    /// <summary>An absolute <c>https</c> address, or null. Links from the server are never opened in other forms.</summary>
    public static Uri? HttpsUri(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>Parses a Google instant such as <c>2026-10-26T09:00:00.0000000Z</c>; a value with no offset counts as UTC.</summary>
    public static DateTimeOffset? ParseInstant(string? text) =>
        !string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}
