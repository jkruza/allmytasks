using System.Text;
using System.Text.RegularExpressions;

namespace AllMyTasks.Core.Providers;

/// <summary>Helpers for building troubleshooting text that is safe to show and log: no addresses, no control characters, bounded size.</summary>
public static partial class DiagnosticText
{
    private static readonly int MaxFrames = 4;

    /// <summary>Replaces anything that looks like an address with <c>&lt;url&gt;</c>, flattens whitespace and control characters, and truncates.</summary>
    public static string Scrub(string? text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = WhitespaceOrControl().Replace(UrlLike().Replace(text, "<url>"), " ").Trim();
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "…";
    }

    /// <summary>The first characters of a response body, scrubbed, for telling an HTML error page from a calendar.</summary>
    public static string Snippet(string? body, int maxLength = 160) => Scrub(body, maxLength);

    /// <summary>
    /// The type, optionally the message, and the first few stack frames of <paramref name="exception"/> and its inner
    /// exceptions. File paths are dropped from frames.
    /// </summary>
    public static string Describe(Exception exception, bool includeMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        var depth = 0;
        for (var current = exception; current is not null && depth < 4; current = current.InnerException, depth++)
        {
            text.Append(depth == 0 ? string.Empty : "\n  caused by ").Append(current.GetType().FullName);
            if (includeMessage && Scrub(current.Message) is { Length: > 0 } message)
            {
                text.Append(": ").Append(message);
            }

            var frames = (current.StackTrace ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxFrames);
            foreach (var frame in frames)
            {
                var end = frame.IndexOf(" in ", StringComparison.Ordinal);
                text.Append("\n    ").Append(end < 0 ? frame : frame[..end]);
            }
        }

        return text.ToString();
    }

    [GeneratedRegex(@"\b(?:https?|webcals?|ftp)://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlLike();

    [GeneratedRegex(@"[\s\p{C}]+")]
    private static partial Regex WhitespaceOrControl();
}
