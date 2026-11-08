using System.Text.RegularExpressions;

namespace AllMyTasks.Core.Notes;

/// <summary>
/// Guesses whether a note is plain text, Markdown or HTML from the text alone. Cheap and deterministic;
/// it prefers <see cref="NoteFormat.Plain"/> when unsure, because Markdown rendering of plain text can eat characters.
/// </summary>
public static partial class NoteFormatDetector
{
    public static NoteFormat Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return NoteFormat.Plain;
        }

        var html = HtmlTag().IsMatch(text);
        var block = MarkdownBlock().IsMatch(text);
        if (html && !block)
        {
            return NoteFormat.Html;
        }

        if (block || MarkdownInline().IsMatch(text))
        {
            return NoteFormat.Markdown;
        }

        return NoteFormat.Plain;
    }

    // A comment, or an opening or closing tag of an element we know. "a < b", "<3" and "<me@example.com>" do not match.
    [GeneratedRegex(
        @"<!--|</?(?:p|br|div|span|a|b|strong|i|em|u|s|strike|del|ins|ul|ol|li|h[1-6]|blockquote|pre|code|kbd|hr|table|thead|tbody|tr|td|th|font|img|sub|sup|small|center|section|article|details|summary|body|html|head|script|style|iframe|frame|frameset|object|embed|applet|noscript|template|svg|math|form|input|button|select|textarea|video|audio|canvas|link|meta|base|title)(?=[\s/>])[^<>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTag();

    // Markers at the start of a line: heading, list item, quote, code fence.
    [GeneratedRegex(
        @"^ {0,3}(?:#{1,6}[ \t]+\S|[-*+][ \t]+\S|\d{1,9}[.)][ \t]+\S|>[ \t]?\S|(?:```|~~~))",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownBlock();

    // Marker pairs inside a line: bold, strikethrough, code span, link. A lone * or _ does not count.
    [GeneratedRegex(
        @"\*\*[^\s*][^*\r\n]*\*\*|~~[^\s~][^~\r\n]*~~|`[^`\r\n]+`|\[[^\]\r\n]+\]\((?:https?:|mailto:)[^)\s]+\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownInline();
}
