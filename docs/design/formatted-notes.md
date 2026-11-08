# Formatted notes

Providers send task notes and event descriptions as plain text, Markdown or HTML, and
rarely say which. The details pane used to show all of them as raw text, so a Jira or
GitHub description full of `**bold**` and `<p>` was hard to read. This doc covers how the
app detects the format and shows basic formatting, without ever running anything from the
note. It is version 1 and stays read-only.

## Goals

- Detect whether a note is plain text, Markdown or HTML, from the text alone.
- Show basic formatting: paragraphs, headings, bold, italic, strikethrough, inline and
  block code, bullet and numbered lists, block quotes, rules, links and line breaks.
- Never execute anything from a note, and never load anything from the network because of
  a note.
- Hide comments (`<!-- ... -->`) in HTML and in Markdown.
- Prefer Microsoft or very widely used libraries.

Not goals: editing, tables, task-list checkboxes, images, syntax highlighting, or exact
GitHub rendering. Anything unsupported degrades to its text.

## Security model

The note is untrusted text from a third party. The rule is that the app never renders
HTML. Notes are parsed into a small document model of our own (below) and the UI builds
native text controls from that model. There is no web view, no HTML renderer, no script
engine, and no HTML is ever passed on to anything.

On top of that, the converter is a whitelist:

| Input | Result |
| --- | --- |
| `<script>`, `<style>`, `<iframe>`, `<frame>`, `<frameset>`, `<object>`, `<embed>`, `<applet>`, `<noscript>`, `<template>`, `<svg>`, `<math>`, `<head>`, `<title>`, `<meta>`, `<link>`, `<base>`, form controls (`<input>`, `<button>`, `<select>`, `<textarea>`) | Dropped **with their content**. Nothing inside them is shown. |
| HTML and Markdown comments | Dropped (see below) |
| `<img>` and Markdown images | Never fetched. An image shows as its alt text in brackets, or nothing if it has none. |
| `<video>`, `<audio>`, `<canvas>`, `<source>`, `<track>`, `<dialog>` | Dropped with their content |
| Event handler attributes (`onclick`, ...), `style`, `class`, `id` | Ignored. Only `href` on `<a>` is read. |
| Other unknown tags | The tag is dropped, its text content is kept |
| Link targets | Only `http`, `https` and `mailto` are kept; `javascript:`, `data:`, `file:`, relative and anything else show as plain text without a link. Links with a user name (`https://google.com@evil.example/`) are refused, as for meeting links |

Other protections: the note is capped at 20 000 characters before parsing (the existing
limit), nesting is capped at 32 levels (deeper content is flattened to text), and the
document is capped at a fixed number of blocks, so a hostile note cannot stall the UI.
Opening a link is always a user click, uses the OS launcher, and shows the full URL as a
tooltip, because the visible text of a link can lie.

### Comments

- HTML: the parser removes comments, including bogus ones such as `<!-->` and
  `<!--->`, and an unterminated `<!--` hides everything after it, exactly as a browser does.
- Markdown: an HTML comment in Markdown is raw HTML, so it is dropped the same way, on a
  line of its own, inside a paragraph, or spanning lines. Link-reference comments
  (`[//]: # (text)`) are definitions that are never displayed.
- Comments inside a code span or a fenced or indented code block are code the author
  meant to show, so they stay visible.
- Because the same note is also searched, comments are removed from the displayed text
  only; search keeps matching the stored note text (unchanged from before).

## Detection

`NoteFormatDetector.Detect(string)` returns `Plain`, `Markdown` or `Html`. It is
heuristic, cheap and deterministic, and errs towards `Plain`, because plain text shown
as Markdown can eat characters (`*`, `_`, `#`).

1. **HTML** when the text contains at least one recognised element tag or a comment:
   an opening or closing tag from a fixed list (`p`, `br`, `div`, `span`, `a`, `b`, `strong`,
   `i`, `em`, `u`, `s`, `ul`, `ol`, `li`, `h1`-`h6`, `blockquote`, `pre`, `code`, `hr`,
   `table`...) or `<!--`. Things like `<john@example.com>`, `a < b` or `<3` are not tags.
2. **Markdown** when at least one clear block marker appears at the start of a line
   (`#` headings followed by a space, `-`, `*`, `+` or `1.` list items followed by a space,
   `>` quotes, fenced code, `---` rules after text) or an inline marker pair
   (`**bold**`, `__bold__`, `` `code` ``, `[text](url)`, `~~strike~~`). A single `*` or
   `_` does not count.
3. Otherwise **Plain**: shown as text, line breaks kept, as today.

A note that is HTML with Markdown inside is treated as HTML. Markdown containing a few
HTML tags is Markdown: raw HTML blocks and inline tags inside it go through the same HTML
whitelist.

Providers may know better (Jira, GitHub and GitLab bodies are Markdown, Outlook bodies
are HTML). A later change can add a hint to `TaskItem` and `CalendarEvent`; detection is
what works for every source today and is the fallback.

## Document model

In Core, `AllMyTasks.Core.Notes`, with no UI references:

```csharp
public enum NoteFormat { Plain, Markdown, Html }

public abstract record NoteBlock;
public sealed record ParagraphBlock(IReadOnlyList<NoteInline> Inlines) : NoteBlock;
public sealed record HeadingBlock(int Level, IReadOnlyList<NoteInline> Inlines) : NoteBlock;
public sealed record CodeBlock(string Text) : NoteBlock;
public sealed record QuoteBlock(IReadOnlyList<NoteBlock> Blocks) : NoteBlock;
public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<NoteBlock>> Items) : NoteBlock;
public sealed record RuleBlock : NoteBlock;

public abstract record NoteInline;
public sealed record TextInline(string Text, bool Bold, bool Italic, bool Strike, bool Code) : NoteInline;
public sealed record LinkInline(Uri Target, IReadOnlyList<NoteInline> Content) : NoteInline;
public sealed record LineBreakInline : NoteInline;

public sealed record NoteDocument(NoteFormat Format, IReadOnlyList<NoteBlock> Blocks);
```

`NoteFormatter.Format(string?)` detects, parses and returns a `NoteDocument`. It never
throws on any input: a parser failure falls back to a single plain paragraph.

## Libraries

| Need | Library | Why |
| --- | --- | --- |
| Markdown | [Markdig](https://github.com/xoofx/markdig) | The standard CommonMark parser for .NET (Alexandre Mutel, BSD-2). Used by Microsoft documentation tooling. It produces an AST and we never use its HTML renderer. Pure managed, so no Avalonia dependency in Core. |
| HTML | [AngleSharp](https://github.com/AngleSharp/AngleSharp) | A parser following the WHATWG HTML5 algorithm (MIT), so odd and broken markup is handled like a browser does. Used as a parser only: scripting, CSS, network loading and iframes are not configured, so nothing is executed or fetched. Microsoft ships no HTML parser for .NET. |

Neither is a Microsoft package. Markdig and AngleSharp are the two most downloaded
libraries of their kind on NuGet. We considered `HtmlAgilityPack` (older, lenient, but
not spec-compliant, which matters when the point is to treat hostile markup like a
browser) and the Avalonia Markdown packages (community packages that render
straight from Markdown, ship their own HTML handling and pull in UI types, and so cannot
be unit-tested or hardened in Core). Both libraries are pinned in `Directory.Packages.props`
and Dependabot already watches NuGet.

## UI

`RichTextView` (a control in `src/AllMyTasks/Controls`) takes a `NoteDocument` and builds
native Avalonia controls: a `SelectableTextBlock` per paragraph or heading with `Run`s
(bold, italic, strikethrough, monospace) and underlined accent-coloured runs for links;
code blocks in a monospace bordered block; a left bar for quotes; indented lists with
bullets or numbers. Text stays selectable. Clicking a link opens it with the OS launcher
(the same call as the existing "Open link" button) and the full URL is the tooltip.
Colours and sizes come from the design tokens, so light, dark and the accent setting
apply.

It replaces the plain `SelectableTextBlock` for Notes in the task details pane and for
Description in the event details pane. The 20 000 character cap stays in the builders.
Lists, search and reminders keep using the plain stored text.

## Links and hidden raw URLs

[hidden-links.md](hidden-links.md) removed raw URLs from the details pane and left URLs inside notes to this work. In notes, a link keeps its visible text (as the author wrote it) and its address is the tooltip, as for the Open link button. A bare URL in a note is still shown as text, because a link text that is the address is what the author chose to show.

## Implementation notes

- Code is in `AllMyTasks.Core.Notes`: `NoteFormatDetector`, `NoteFormatter` (entry point), `HtmlNoteParser`, `MarkdownNoteParser`, `CommentStripper` and `NoteLinks`. The view models call `NoteFormatter.Format` once when the pane opens.
- Markdown comments are removed by `CommentStripper` before parsing, because CommonMark does not let an inline comment span a blank line and a parser alone could leave half of one visible. It keeps comments inside inline code spans and top-level fenced blocks. Comments inside an indented code block or a fence nested in a quote or list are removed too (hiding wins over showing there). Markdig's own comment handling stays as a second layer.
- Soft line breaks in Markdown are kept as line breaks, as GitHub does in comments.
- Plain text is shown through the same control, split into paragraphs on blank lines with single line breaks kept.
- If a parser ever throws, the pane shows the text as plain paragraphs with comments removed.
- Chosen package versions: Markdig 1.6.0 and AngleSharp 1.10.0 (1.4.0 has a moderate advisory, GHSA-pgww-w46g-26qg).

## Tests

Core only, in `tests/AllMyTasks.Core.Tests/NoteFormatTests.cs`: detection (HTML, Markdown, plain and the
false-positive cases above), Markdown and HTML conversion for each supported element,
and the security table: script, style and iframe content never appears; comments are hidden
in HTML and in Markdown in every position, but kept inside code; `javascript:`, `data:` and
`file:` links lose their target; images are not turned into links; deep nesting, huge
input and unterminated constructs do not throw or hang.

## Not verified

The control has not been looked at on a real desktop from the cloud, so Jarek should check
light and dark themes, a long note, and clicking a link.
