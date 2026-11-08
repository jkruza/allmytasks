using System.Text.RegularExpressions;

namespace AllMyTasks.Core.Notes;

/// <summary>
/// Turns the text of a note or description into a <see cref="NoteDocument"/>: detects the format, parses it and keeps
/// only a whitelist of formatting. Nothing is executed, rendered as HTML or fetched; see docs/design/formatted-notes.md.
/// </summary>
public static class NoteFormatter
{
    /// <summary>Longest text parsed. The details builders already cap at 20 000; this guards other callers.</summary>
    public const int MaxInputLength = 50_000;

    public static NoteDocument Format(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new NoteDocument(NoteFormat.Plain, []);
        }

        if (text.Length > MaxInputLength)
        {
            text = text[..MaxInputLength];
        }

        var format = NoteFormatDetector.Detect(text);
        try
        {
            var budget = new NoteBudget();
            return format switch
            {
                NoteFormat.Html => new NoteDocument(format, HtmlNoteParser.Parse(text, budget)),
                NoteFormat.Markdown => new NoteDocument(format, MarkdownNoteParser.Parse(text, budget)),
                _ => new NoteDocument(format, PlainBlocks(text)),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or RegexMatchTimeoutException or IndexOutOfRangeException or NullReferenceException)
        {
            // A parser bug must never take the details pane down. Show the text with comments removed.
            return new NoteDocument(NoteFormat.Plain, PlainBlocks(CommentStripper.Strip(text)));
        }
    }

    private static List<NoteBlock> PlainBlocks(string text)
    {
        var blocks = new List<NoteBlock>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var inlines = new List<NoteInline>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (inlines.Count > 0)
            {
                inlines.Add(new LineBreakInline());
            }

            inlines.Add(new TextInline(line.TrimEnd()));
        }

        Flush();
        return blocks;

        void Flush()
        {
            if (inlines.Count > 0 && blocks.Count < NoteBudget.MaxBlocks)
            {
                blocks.Add(new ParagraphBlock([.. inlines]));
            }

            inlines.Clear();
        }
    }
}
