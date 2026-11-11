using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AllMyTasks.Core.Notes;

/// <summary>
/// Converts HTML to note blocks. AngleSharp is used as a parser only: no scripting, no stylesheets, no network.
/// Only a whitelist of elements produces output; everything else is dropped or reduced to its text.
/// </summary>
internal static class HtmlNoteParser
{
    /// <summary>Elements dropped together with everything inside them.</summary>
    public static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "frame", "frameset", "noframes", "object", "embed", "applet", "noscript", "template",
        "svg", "math", "head", "title", "meta", "link", "base", "input", "button", "select", "textarea", "option",
        "optgroup", "datalist", "canvas", "audio", "video", "source", "track", "dialog",
    };

    private static readonly HashSet<string> Blocky = new(StringComparer.Ordinal)
    {
        "div", "section", "article", "header", "footer", "main", "aside", "nav", "figure", "figcaption", "details", "summary",
        "dl", "dt", "dd", "address", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "form", "fieldset",
        "legend", "li", "center", "body", "html", "hgroup", "menu", "dir",
    };

    public static List<NoteBlock> Parse(string html, NoteBudget budget)
    {
        var document = new HtmlParser().ParseDocument(html);
        var root = (INode?)document.Body ?? document.DocumentElement;
        var blocks = new List<NoteBlock>();
        if (root is null)
        {
            return blocks;
        }

        new Converter(budget).Blocks(root, 0, blocks);
        return blocks;
    }

    private readonly record struct Style(bool Bold, bool Italic, bool Strike, bool Code);

    private sealed class Frame
    {
        public List<NoteBlock> Blocks { get; init; } = [];

        public List<NoteInline> Inlines { get; } = [];
    }

    private sealed class Converter(NoteBudget budget)
    {
        public void Blocks(INode container, int depth, List<NoteBlock> into)
        {
            var frame = new Frame { Blocks = into };
            Walk(container, frame, default, depth, false);
            Flush(frame);
        }

        private void Walk(INode parent, Frame frame, Style style, int depth, bool inLink)
        {
            foreach (var node in parent.ChildNodes)
            {
                if (node is IText text)
                {
                    AddText(frame, Collapse(text.Data), style);
                }
                else if (node is IElement element)
                {
                    Element(element, frame, style, depth, inLink);
                }
            }
        }

        private void Element(IElement element, Frame frame, Style style, int depth, bool inLink)
        {
            var name = element.LocalName;
            if (Dropped.Contains(name) || element.NamespaceUri != "http://www.w3.org/1999/xhtml")
            {
                return;
            }

            if (depth >= NoteBudget.MaxDepth)
            {
                AddText(frame, Collapse(TextOf(element, false)), style);
                return;
            }

            switch (name)
            {
                case "br":
                    frame.Inlines.Add(new LineBreakInline());
                    break;
                case "hr":
                    Flush(frame);
                    AddBlock(frame, new RuleBlock());
                    break;
                case "pre":
                    Flush(frame);
                    var code = TextOf(element, true).Trim('\r', '\n').TrimEnd();
                    if (code.Length > 0)
                    {
                        AddBlock(frame, new CodeBlock(code));
                    }

                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush(frame);
                    var inner = new Frame();
                    Walk(element, inner, style, depth + 1, inLink);
                    var title = InlineTools.Tidy(inner.Inlines);
                    if (title.Count > 0)
                    {
                        AddBlock(frame, new HeadingBlock(name[1] - '0', title));
                    }

                    foreach (var block in inner.Blocks)
                    {
                        AddBlock(frame, block);
                    }

                    break;
                case "ul" or "ol":
                    Flush(frame);
                    List(element, frame, depth, name == "ol");
                    break;
                case "blockquote":
                    Flush(frame);
                    var quoted = new List<NoteBlock>();
                    Blocks(element, depth + 1, quoted);
                    if (quoted.Count > 0)
                    {
                        AddBlock(frame, new QuoteBlock(quoted));
                    }

                    break;
                case "p":
                    Flush(frame);
                    Walk(element, frame, style, depth + 1, inLink);
                    Flush(frame);
                    break;
                case "a":
                    Link(element, frame, style, depth, inLink);
                    break;
                case "img":
                    var alt = element.GetAttribute("alt")?.Trim();
                    if (!string.IsNullOrEmpty(alt))
                    {
                        AddText(frame, $"[{Collapse(alt)}]", default);
                    }

                    break;
                case "b" or "strong":
                    Walk(element, frame, style with { Bold = true }, depth + 1, inLink);
                    break;
                case "i" or "em" or "cite" or "dfn" or "var":
                    Walk(element, frame, style with { Italic = true }, depth + 1, inLink);
                    break;
                case "s" or "strike" or "del":
                    Walk(element, frame, style with { Strike = true }, depth + 1, inLink);
                    break;
                case "code" or "kbd" or "samp" or "tt":
                    Walk(element, frame, style with { Code = true }, depth + 1, inLink);
                    break;
                default:
                    if (Blocky.Contains(name))
                    {
                        Flush(frame);
                        Walk(element, frame, style, depth + 1, inLink);
                        Flush(frame);
                    }
                    else
                    {
                        // Unknown or purely presentational tag: drop the tag, keep the text.
                        Walk(element, frame, style, depth + 1, inLink);
                    }

                    break;
            }
        }

        private void List(IElement list, Frame frame, int depth, bool ordered)
        {
            var items = new List<IReadOnlyList<NoteBlock>>();
            foreach (var child in list.Children)
            {
                if (Dropped.Contains(child.LocalName))
                {
                    continue;
                }

                var blocks = new List<NoteBlock>();
                if (child.LocalName == "li")
                {
                    Blocks(child, depth + 1, blocks);
                }
                else
                {
                    var wrapper = new Frame { Blocks = blocks };
                    Element(child, wrapper, default, depth + 1, false);
                    Flush(wrapper);
                }

                if (blocks.Count > 0)
                {
                    items.Add(blocks);
                }
            }

            if (items.Count > 0)
            {
                var start = ordered && int.TryParse(list.GetAttribute("start"), out var s) && s is >= 0 and < 1_000_000 ? s : 1;
                AddBlock(frame, new ListBlock(ordered, start, items));
            }
        }

        private void Link(IElement element, Frame frame, Style style, int depth, bool inLink)
        {
            if (inLink || !NoteLinks.TryCreate(element.GetAttribute("href"), out var target))
            {
                // Unsafe or nested link: keep the visible text, drop the target.
                Walk(element, frame, style, depth + 1, inLink);
                return;
            }

            var inner = new Frame();
            Walk(element, inner, style, depth + 1, true);
            if (inner.Blocks.Count > 0)
            {
                Flush(frame);
                foreach (var block in inner.Blocks)
                {
                    AddBlock(frame, block);
                }
            }

            var content = InlineTools.Tidy(inner.Inlines);
            if (content.Count > 0)
            {
                frame.Inlines.Add(new LinkInline(target, content));
            }
        }

        private static void AddText(Frame frame, string text, Style style)
        {
            if (text.Length == 0)
            {
                return;
            }

            var inlines = frame.Inlines;
            if (text[0] == ' ' && inlines.Count > 0 && inlines[^1] is TextInline { Text: [.., ' '] })
            {
                text = text[1..];
            }

            InlineTools.AddText(inlines, text, style.Bold, style.Italic, style.Strike, style.Code);
        }

        private void AddBlock(Frame frame, NoteBlock block)
        {
            if (budget.TryTakeBlock())
            {
                frame.Blocks.Add(block);
            }
        }

        private void Flush(Frame frame)
        {
            var inlines = InlineTools.Tidy(frame.Inlines);
            frame.Inlines.Clear();
            if (inlines.Count > 0)
            {
                AddBlock(frame, new ParagraphBlock(inlines));
            }
        }
    }

    /// <summary>Collapses runs of HTML whitespace into one space, as a browser does outside <c>pre</c>.</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        if (space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>All text under a node, skipping dropped elements and comments. Iterative so deep trees cannot overflow the stack.</summary>
    private static string TextOf(INode root, bool keepLineBreaks)
    {
        var sb = new StringBuilder();
        var stack = new Stack<INode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case IText text:
                    sb.Append(text.Data);
                    break;
                case IElement element when !ReferenceEquals(element, root) && Dropped.Contains(element.LocalName):
                    break;
                case IElement { LocalName: "br" } when keepLineBreaks:
                    sb.Append('\n');
                    break;
                case IElement or IDocumentFragment:
                    for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
                    {
                        stack.Push(child);
                    }

                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }
}
