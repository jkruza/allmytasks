using System.Text;

namespace AllMyTasks.Core.Notes;

/// <summary>The markup a note was written in, as guessed by <see cref="NoteFormatDetector"/>.</summary>
public enum NoteFormat
{
    Plain,
    Markdown,
    Html,
}

/// <summary>A block of a formatted note. The UI builds native controls from these; no markup reaches it.</summary>
public abstract record NoteBlock;

/// <summary>A run of inline content.</summary>
public sealed record ParagraphBlock(IReadOnlyList<NoteInline> Inlines) : NoteBlock;

/// <summary>A heading; <see cref="Level"/> is 1 (largest) to 6.</summary>
public sealed record HeadingBlock(int Level, IReadOnlyList<NoteInline> Inlines) : NoteBlock;

/// <summary>Preformatted text, shown in a monospace font.</summary>
public sealed record CodeBlock(string Text) : NoteBlock;

/// <summary>A quotation containing other blocks.</summary>
public sealed record QuoteBlock(IReadOnlyList<NoteBlock> Blocks) : NoteBlock;

/// <summary>A bullet or numbered list. Each item is a list of blocks.</summary>
public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<NoteBlock>> Items) : NoteBlock;

/// <summary>A horizontal rule.</summary>
public sealed record RuleBlock : NoteBlock;

/// <summary>Inline content of a paragraph or heading.</summary>
public abstract record NoteInline;

/// <summary>Text with simple styling.</summary>
public sealed record TextInline(string Text, bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false) : NoteInline;

/// <summary>A link. <see cref="Target"/> is always http, https or mailto; other links are shown as plain text.</summary>
public sealed record LinkInline(Uri Target, IReadOnlyList<NoteInline> Content) : NoteInline;

/// <summary>A forced line break.</summary>
public sealed record LineBreakInline : NoteInline;

/// <summary>A note converted to blocks. <see cref="Format"/> is what was detected.</summary>
public sealed record NoteDocument(NoteFormat Format, IReadOnlyList<NoteBlock> Blocks)
{
    /// <summary>The visible text without formatting, blocks separated by a blank line. Used by tests and accessibility.</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        AppendBlocks(sb, Blocks);
        return sb.ToString().Trim();
    }

    private static void AppendBlocks(StringBuilder sb, IReadOnlyList<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    AppendInlines(sb, p.Inlines);
                    break;
                case HeadingBlock h:
                    AppendInlines(sb, h.Inlines);
                    break;
                case CodeBlock c:
                    sb.Append(c.Text);
                    break;
                case QuoteBlock q:
                    AppendBlocks(sb, q.Blocks);
                    break;
                case ListBlock l:
                    foreach (var item in l.Items)
                    {
                        AppendBlocks(sb, item);
                    }

                    break;
                default:
                    break;
            }

            sb.Append("\n\n");
        }
    }

    private static void AppendInlines(StringBuilder sb, IReadOnlyList<NoteInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    sb.Append(t.Text);
                    break;
                case LinkInline l:
                    AppendInlines(sb, l.Content);
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
                default:
                    break;
            }
        }
    }
}
