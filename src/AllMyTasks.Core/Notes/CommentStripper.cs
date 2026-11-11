using System.Text;

namespace AllMyTasks.Core.Notes;

/// <summary>
/// Removes <c>&lt;!-- ... --&gt;</c> comments from Markdown source before it is parsed. CommonMark does not let an
/// inline comment span a blank line, so a parser alone can leave half of one visible. Comments inside inline code
/// spans and top-level fenced code blocks are kept, because the author meant to show them.
/// </summary>
internal static class CommentStripper
{
    internal const int MaxPasses = 16;
    internal const int MaxSpanSearch = 2_000;

    public static string Strip(string text)
    {
        // Removing one comment can join the pieces around it into a new one ("<!<!-- -->-- x -->"), so repeat.
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = StripOnce(text);
            if (next.Length == text.Length)
            {
                return next;
            }

            text = next;
        }

        return text;
    }

    private static string StripOnce(string text)
    {
        if (!text.Contains("<!--", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        var lineStart = true;
        while (i < text.Length)
        {
            if (lineStart && FenceEnd(text, i) is var fenceEnd and > 0)
            {
                sb.Append(text, i, fenceEnd - i);
                i = fenceEnd;
                continue;
            }

            var c = text[i];
            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindClosingRun(text, i + run, run);
                var copyTo = close < 0 ? i + run : close + run;
                sb.Append(text, i, copyTo - i);
                i = copyTo;
                lineStart = false;
                continue;
            }

            if (c == '<' && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i = CommentEnd(text, i + 4);
                continue;
            }

            sb.Append(c);
            lineStart = c == '\n';
            i++;
        }

        return sb.ToString();
    }

    private static int CommentEnd(string text, int from)
    {
        // "<!-->" and "<!--->" are complete (empty) comments in HTML5.
        if (from < text.Length && text[from] == '>')
        {
            return from + 1;
        }

        if (from + 1 < text.Length && text[from] == '-' && text[from + 1] == '>')
        {
            return from + 2;
        }

        var plain = text.IndexOf("-->", from, StringComparison.Ordinal);
        var bang = text.IndexOf("--!>", from, StringComparison.Ordinal);
        if (plain < 0 && bang < 0)
        {
            return text.Length; // unterminated: hides the rest, like a browser
        }

        return bang >= 0 && (plain < 0 || bang < plain) ? bang + 4 : plain + 3;
    }

    private static int RunLength(string text, int start, char c)
    {
        var end = start;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }

        return end - start;
    }

    // Start of the next run of exactly `length` backticks before a blank line, or -1.
    private static int FindClosingRun(string text, int from, int length)
    {
        var limit = Math.Min(text.Length, from + MaxSpanSearch);
        var newlines = 0;
        for (var i = from; i < limit; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                if (++newlines > 1)
                {
                    return -1;
                }

                continue;
            }

            if (c is not (' ' or '\t' or '\r'))
            {
                newlines = 0;
            }

            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                if (run == length)
                {
                    return i;
                }

                i += run - 1;
            }
        }

        return -1;
    }

    // If a fenced code block opens at `start` (a line start), the index just past its closing line; otherwise -1.
    private static int FenceEnd(string text, int start)
    {
        var i = start;
        var indent = 0;
        while (i < text.Length && text[i] == ' ' && indent < 4)
        {
            i++;
            indent++;
        }

        if (indent > 3 || i >= text.Length || text[i] is not ('`' or '~'))
        {
            return -1;
        }

        var fence = text[i];
        var length = RunLength(text, i, fence);
        if (length < 3)
        {
            return -1;
        }

        var lineEnd = text.IndexOf('\n', i);
        while (lineEnd >= 0)
        {
            var line = lineEnd + 1;
            var next = text.IndexOf('\n', line);
            var end = next < 0 ? text.Length : next;
            if (IsClosingFence(text.AsSpan(line, end - line), fence, length))
            {
                return next < 0 ? text.Length : next + 1;
            }

            lineEnd = next;
        }

        return text.Length;
    }

    private static bool IsClosingFence(ReadOnlySpan<char> line, char fence, int length)
    {
        var s = line.TrimEnd();
        var indent = s.Length - s.TrimStart(' ').Length;
        s = s.TrimStart(' ');
        return indent <= 3 && s.Length >= length && s.Trim(fence).Length == 0;
    }
}
