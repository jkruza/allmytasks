using System.Text.RegularExpressions;
using Markdig;
using Md = Markdig.Syntax;
using MdInl = Markdig.Syntax.Inlines;

namespace AllMyTasks.Core.Notes;

/// <summary>
/// Converts Markdown to note blocks through Markdig's syntax tree; Markdig's HTML renderer is never used.
/// Raw HTML inside the Markdown goes through the same whitelist as an HTML note.
/// </summary>
internal static partial class MarkdownNoteParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    public static List<NoteBlock> Parse(string markdown, NoteBudget budget)
    {
        var document = Markdig.Markdown.Parse(CommentStripper.Strip(markdown), Pipeline);
        var blocks = new List<NoteBlock>();
        new Converter(budget).Blocks(document, 0, blocks);
        return blocks;
    }

    [GeneratedRegex(@"^<(/?)([A-Za-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagName();

    private sealed class Converter(NoteBudget budget)
    {
        public void Blocks(Md.ContainerBlock container, int depth, List<NoteBlock> into)
        {
            if (depth >= NoteBudget.MaxDepth)
            {
                return;
            }

            foreach (var block in container)
            {
                switch (block)
                {
                    case Md.HeadingBlock heading:
                        Add(into, new HeadingBlock(Math.Clamp(heading.Level, 1, 6), Inlines(heading.Inline)));
                        break;
                    case Md.ParagraphBlock paragraph:
                        var inlines = Inlines(paragraph.Inline);
                        if (inlines.Count > 0)
                        {
                            Add(into, new ParagraphBlock(inlines));
                        }

                        break;
                    case Md.HtmlBlock html:
                        foreach (var converted in HtmlNoteParser.Parse(html.Lines.ToString(), budget))
                        {
                            into.Add(converted);
                        }

                        break;
                    case Md.CodeBlock code:
                        var text = code.Lines.ToString().Trim('\r', '\n').TrimEnd();
                        if (text.Length > 0)
                        {
                            Add(into, new CodeBlock(text));
                        }

                        break;
                    case Md.ThematicBreakBlock:
                        Add(into, new RuleBlock());
                        break;
                    case Md.QuoteBlock quote:
                        var quoted = new List<NoteBlock>();
                        Blocks(quote, depth + 1, quoted);
                        if (quoted.Count > 0)
                        {
                            Add(into, new QuoteBlock(quoted));
                        }

                        break;
                    case Md.ListBlock list:
                        var items = new List<IReadOnlyList<NoteBlock>>();
                        foreach (var item in list.OfType<Md.ListItemBlock>())
                        {
                            var itemBlocks = new List<NoteBlock>();
                            Blocks(item, depth + 1, itemBlocks);
                            items.Add(itemBlocks);
                        }

                        if (items.Count > 0)
                        {
                            var start = int.TryParse(list.OrderedStart, out var s) && s is >= 0 and < 1_000_000 ? s : 1;
                            Add(into, new ListBlock(list.IsOrdered, start, items));
                        }

                        break;
                    case Md.LinkReferenceDefinitionGroup:
                        break; // "[//]: # (comment)" and other definitions are never shown
                    case Md.ContainerBlock inner:
                        Blocks(inner, depth + 1, into);
                        break;
                    case Md.LeafBlock { Inline: not null } leaf:
                        var fallback = Inlines(leaf.Inline);
                        if (fallback.Count > 0)
                        {
                            Add(into, new ParagraphBlock(fallback));
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        private void Add(List<NoteBlock> into, NoteBlock block)
        {
            if (budget.TryTakeBlock())
            {
                into.Add(block);
            }
        }

        private static List<NoteInline> Inlines(MdInl.ContainerInline? container)
        {
            var sink = new List<NoteInline>();
            if (container is not null)
            {
                new InlineConverter().Convert(container.FirstChild, sink, 0);
            }

            return InlineTools.Tidy(sink);
        }
    }

    private sealed class InlineConverter
    {
        private int _bold;
        private int _italic;
        private int _strike;
        private int _code;
        private bool _inLink;
        private string? _hiddenTag;

        public void Convert(MdInl.Inline? first, List<NoteInline> sink, int depth)
        {
            if (depth >= NoteBudget.MaxDepth)
            {
                return;
            }

            for (var inline = first; inline is not null; inline = inline.NextSibling)
            {
                if (inline is MdInl.HtmlInline tag)
                {
                    Tag(tag.Tag, sink);
                    continue;
                }

                if (_hiddenTag is not null)
                {
                    continue; // inside <script>, <iframe>... typed as raw inline HTML
                }

                switch (inline)
                {
                    case MdInl.LiteralInline literal:
                        Text(sink, literal.Content.ToString());
                        break;
                    case MdInl.CodeInline code:
                        InlineTools.AddText(sink, code.Content, _bold > 0, _italic > 0, _strike > 0, true);
                        break;
                    case MdInl.LineBreakInline:
                        sink.Add(new LineBreakInline());
                        break;
                    case MdInl.HtmlEntityInline entity:
                        Text(sink, entity.Transcoded.ToString());
                        break;
                    case MdInl.AutolinkInline auto:
                        var href = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                        if (!_inLink && NoteLinks.TryCreate(href, out var autoTarget))
                        {
                            sink.Add(new LinkInline(autoTarget, [new TextInline(auto.Url)]));
                        }
                        else
                        {
                            Text(sink, auto.Url);
                        }

                        break;
                    case MdInl.LinkInline link:
                        Link(link, sink, depth);
                        break;
                    case MdInl.EmphasisInline emphasis:
                        Emphasis(emphasis, sink, depth);
                        break;
                    case MdInl.ContainerInline container:
                        Convert(container.FirstChild, sink, depth + 1);
                        break;
                    default:
                        break;
                }
            }
        }

        private void Text(List<NoteInline> sink, string text) =>
            InlineTools.AddText(sink, text, _bold > 0, _italic > 0, _strike > 0, _code > 0);

        private void Emphasis(MdInl.EmphasisInline emphasis, List<NoteInline> sink, int depth)
        {
            var bold = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2;
            var italic = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount is 1 or 3;
            var strike = emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2;
            _bold += bold ? 1 : 0;
            _italic += italic ? 1 : 0;
            _strike += strike ? 1 : 0;
            Convert(emphasis.FirstChild, sink, depth + 1);
            _bold -= bold ? 1 : 0;
            _italic -= italic ? 1 : 0;
            _strike -= strike ? 1 : 0;
        }

        private void Link(MdInl.LinkInline link, List<NoteInline> sink, int depth)
        {
            if (link.IsImage)
            {
                // Images are never fetched; show the alt text.
                var alt = new List<NoteInline>();
                var saved = _inLink;
                _inLink = true;
                Convert(link.FirstChild, alt, depth + 1);
                _inLink = saved;
                var text = new Notes.NoteDocument(NoteFormat.Plain, [new ParagraphBlock(alt)]).ToPlainText();
                if (text.Length > 0)
                {
                    Text(sink, $"[{text}]");
                }

                return;
            }

            if (_inLink || !NoteLinks.TryCreate(link.Url, out var target))
            {
                Convert(link.FirstChild, sink, depth + 1);
                return;
            }

            var content = new List<NoteInline>();
            _inLink = true;
            Convert(link.FirstChild, content, depth + 1);
            _inLink = false;
            content = InlineTools.Tidy(content);
            if (content.Count > 0)
            {
                sink.Add(new LinkInline(target, content));
            }
        }

        // Raw inline HTML inside Markdown: comments vanish, <br> breaks the line, a few tags style text,
        // dangerous elements hide everything up to their closing tag, all other tags are dropped.
        private void Tag(string raw, List<NoteInline> sink)
        {
            if (raw.StartsWith("<!", StringComparison.Ordinal) || raw.StartsWith("<?", StringComparison.Ordinal))
            {
                return;
            }

            var match = TagName().Match(raw);
            if (!match.Success)
            {
                return;
            }

            var closing = match.Groups[1].Length > 0;
            var name = match.Groups[2].Value.ToLowerInvariant();
            if (_hiddenTag is not null)
            {
                if (closing && name == _hiddenTag)
                {
                    _hiddenTag = null;
                }

                return;
            }

            if (HtmlNoteParser.Dropped.Contains(name))
            {
                if (!closing && !raw.EndsWith("/>", StringComparison.Ordinal))
                {
                    _hiddenTag = name;
                }

                return;
            }

            var delta = closing ? -1 : 1;
            switch (name)
            {
                case "br":
                    sink.Add(new LineBreakInline());
                    break;
                case "b" or "strong":
                    _bold = Math.Max(0, _bold + delta);
                    break;
                case "i" or "em":
                    _italic = Math.Max(0, _italic + delta);
                    break;
                case "s" or "strike" or "del":
                    _strike = Math.Max(0, _strike + delta);
                    break;
                case "code" or "kbd" or "samp" or "tt":
                    _code = Math.Max(0, _code + delta);
                    break;
                default:
                    break;
            }
        }
    }
}
