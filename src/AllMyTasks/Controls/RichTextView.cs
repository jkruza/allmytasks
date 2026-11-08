using AllMyTasks.Core.Notes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AllMyTasks.Controls;

/// <summary>
/// Shows a <see cref="NoteDocument"/> with native text controls: paragraphs, headings, lists, quotes, code and links.
/// No markup, web view or script engine is involved; the document only holds whitelisted formatting. Text is selectable,
/// and a link opens only on a click with the OS launcher, with its full address as the tooltip.
/// </summary>
public sealed class RichTextView : ContentControl
{
    public static readonly StyledProperty<NoteDocument?> DocumentProperty =
        AvaloniaProperty.Register<RichTextView, NoteDocument?>(nameof(Document));

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    static RichTextView() => DocumentProperty.Changed.AddClassHandler<RichTextView>((view, _) => view.Rebuild());

    public NoteDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void Rebuild()
    {
        if (Document is not { } document)
        {
            Content = null;
            return;
        }

        var panel = new StackPanel { Spacing = 6 };
        AddBlocks(panel, document.Blocks);
        Content = panel;
    }

    private void AddBlocks(Panel panel, IReadOnlyList<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            panel.Children.Add(Build(block));
        }
    }

    private Control Build(NoteBlock block) => block switch
    {
        ParagraphBlock p => Text(p.Inlines, null),
        HeadingBlock h => Text(h.Inlines, h.Level),
        CodeBlock c => CodeControl(c),
        QuoteBlock q => QuoteControl(q),
        ListBlock l => ListControl(l),
        _ => RuleControl(),
    };

    private Border RuleControl()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
        rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("AmtBorderBrush"));
        return rule;
    }

    private Border CodeControl(CodeBlock code)
    {
        var text = new SelectableTextBlock { Text = code.Text, FontFamily = Mono, TextWrapping = TextWrapping.NoWrap };
        var border = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = text },
        };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AmtRaisedBrush"));
        return border;
    }

    private Border QuoteControl(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 6 };
        AddBlocks(inner, quote.Blocks);
        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Child = inner };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AmtBorderBrush"));
        return border;
    }

    private StackPanel ListControl(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0, 0, 0) };
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var content = new StackPanel { Spacing = 4 };
            AddBlocks(content, item);
            var marker = new TextBlock { Text = list.Ordered ? $"{number++}." : "•", MinWidth = 18, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            row.Children.Add(marker);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private SelectableTextBlock Text(IReadOnlyList<NoteInline> inlines, int? heading)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (heading is { } level)
        {
            block.FontWeight = FontWeight.SemiBold;
            if (level == 1)
            {
                block.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("AmtFontSizeHeading"));
            }
            else if (level == 2)
            {
                block.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("AmtFontSizeTitle"));
            }
        }

        var links = new List<(int Start, int End, Uri Target)>();
        var offset = 0;
        AddInlines(block.Inlines!, inlines, links, ref offset, null);
        if (links.Count > 0)
        {
            AttachLinks(block, links);
        }

        return block;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<NoteInline> inlines, List<(int Start, int End, Uri Target)> links, ref int offset, Uri? link)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    var run = new Run(t.Text);
                    if (t.Bold)
                    {
                        run.FontWeight = FontWeight.Bold;
                    }

                    if (t.Italic)
                    {
                        run.FontStyle = FontStyle.Italic;
                    }

                    if (t.Strike)
                    {
                        run.TextDecorations = TextDecorations.Strikethrough;
                    }

                    if (t.Code)
                    {
                        run.FontFamily = Mono;
                        run.Background = null;
                    }

                    if (link is not null)
                    {
                        run.TextDecorations = TextDecorations.Underline;
                        run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AmtAccentBrush"));
                        links.Add((offset, offset + t.Text.Length, link));
                    }

                    target.Add(run);
                    offset += t.Text.Length;
                    break;
                case LineBreakInline:
                    // A newline inside a Run, not a LineBreak element, so character offsets stay exact for link hit testing.
                    target.Add(new Run("\n"));
                    offset += 1;
                    break;
                case LinkInline l:
                    AddInlines(target, l.Content, links, ref offset, l.Target);
                    break;
                default:
                    break;
            }
        }
    }

    private void AttachLinks(SelectableTextBlock block, List<(int Start, int End, Uri Target)> links)
    {
        Uri? At(PointerEventArgs e)
        {
            var layout = block.TextLayout;
            var hit = layout.HitTestPoint(e.GetPosition(block));
            if (!hit.IsInside)
            {
                return null;
            }

            foreach (var (start, end, target) in links)
            {
                if (hit.TextPosition >= start && hit.TextPosition < end)
                {
                    return target;
                }
            }

            return null;
        }

        block.PointerMoved += (_, e) =>
        {
            var target = At(e);
            block.Cursor = target is null ? new Cursor(StandardCursorType.Ibeam) : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(block, target?.AbsoluteUri);
        };
        block.AddHandler(
            PointerReleasedEvent,
            async (_, e) =>
            {
                // A drag that selected text is not a click on a link.
                if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd)
                {
                    return;
                }

                if (At(e) is { } target && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                {
                    await launcher.LaunchUriAsync(target);
                }
            },
            handledEventsToo: true);
    }
}
