using AllMyTasks.Core.Notes;

namespace AllMyTasks.Core.Tests;

public class NoteFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Buy milk and eggs")]
    [InlineData("Call Anna at 5 if a < b and 3 > 2")]
    [InlineData("I <3 this, mail <me@example.com>")]
    [InlineData("snake_case and 2*3 = 6 and a_b_c")]
    [InlineData("Line one\nLine two\n\nLine four")]
    public void Plain_text_stays_plain(string? text) => Assert.Equal(NoteFormat.Plain, NoteFormatDetector.Detect(text));

    [Theory]
    [InlineData("<p>Hello</p>")]
    [InlineData("Hi<br>there")]
    [InlineData("<div class=\"x\">a</div>")]
    [InlineData("<B>loud</B>")]
    [InlineData("text <!-- hidden --> text")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<iframe src=\"https://x\"></iframe>")]
    [InlineData("<a href=\"https://example.com\">link</a>")]
    public void Html_is_detected(string text) => Assert.Equal(NoteFormat.Html, NoteFormatDetector.Detect(text));

    [Theory]
    [InlineData("# Title\n\ntext")]
    [InlineData("- one\n- two")]
    [InlineData("* one")]
    [InlineData("1. one\n2. two")]
    [InlineData("> quoted")]
    [InlineData("```\ncode\n```")]
    [InlineData("some **bold** text")]
    [InlineData("see [docs](https://example.com/docs)")]
    [InlineData("use `dotnet test` here")]
    [InlineData("~~gone~~")]
    [InlineData("# Title\n\n<details><summary>More</summary>x</details>")]
    public void Markdown_is_detected(string text) => Assert.Equal(NoteFormat.Markdown, NoteFormatDetector.Detect(text));

    [Fact]
    public void Empty_notes_have_no_blocks()
    {
        Assert.Empty(NoteFormatter.Format(null).Blocks);
        Assert.Empty(NoteFormatter.Format("   \n ").Blocks);
    }

    [Fact]
    public void Plain_text_keeps_paragraphs_and_line_breaks()
    {
        var doc = NoteFormatter.Format("one\ntwo\n\nthree");
        Assert.Equal(NoteFormat.Plain, doc.Format);
        Assert.Equal(2, doc.Blocks.Count);
        var first = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Collection(first.Inlines, i => Assert.Equal("one", ((TextInline)i).Text), i => Assert.IsType<LineBreakInline>(i), i => Assert.Equal("two", ((TextInline)i).Text));
    }

    [Fact]
    public void Markdown_formatting_is_converted()
    {
        var doc = NoteFormatter.Format("# Title\n\nSome **bold**, *italic*, ~~gone~~ and `code`.\n\n- a\n- b\n\n3. x\n4. y\n\n> quote\n\n---\n\n```\nlet x = 1;\n```");
        Assert.Equal(NoteFormat.Markdown, doc.Format);
        var heading = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(1, heading.Level);
        var paragraph = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "bold", Bold: true });
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "italic", Italic: true });
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "gone", Strike: true });
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "code", Code: true });
        var bullets = Assert.IsType<ListBlock>(doc.Blocks[2]);
        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        var numbers = Assert.IsType<ListBlock>(doc.Blocks[3]);
        Assert.True(numbers.Ordered);
        Assert.Equal(3, numbers.Start);
        Assert.IsType<QuoteBlock>(doc.Blocks[4]);
        Assert.IsType<RuleBlock>(doc.Blocks[5]);
        Assert.Equal("let x = 1;", Assert.IsType<CodeBlock>(doc.Blocks[6]).Text);
    }

    [Fact]
    public void Markdown_links_keep_safe_targets_and_images_become_alt_text()
    {
        var doc = NoteFormatter.Format("See [the docs](https://example.com/a) and ![logo](https://tracker.example/pixel.png) or <https://example.org>.");
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        var links = paragraph.Inlines.OfType<LinkInline>().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/a", links[0].Target.AbsoluteUri);
        Assert.Contains("[logo]", doc.ToPlainText());
        Assert.DoesNotContain("pixel.png", doc.ToPlainText());
    }

    [Fact]
    public void Html_formatting_is_converted()
    {
        var doc = NoteFormatter.Format("<h2>Plan</h2><p>Some <b>bold</b> and <i>italic</i> <a href=\"https://example.com\">link</a>.</p><ul><li>a</li><li>b<ol start=\"5\"><li>deep</li></ol></li></ul><blockquote>q</blockquote><pre>x\n  y</pre><hr>");
        Assert.Equal(NoteFormat.Html, doc.Format);
        Assert.Equal(2, Assert.IsType<HeadingBlock>(doc.Blocks[0]).Level);
        var paragraph = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "bold", Bold: true });
        Assert.Contains(paragraph.Inlines, i => i is TextInline { Text: "italic", Italic: true });
        Assert.Single(paragraph.Inlines.OfType<LinkInline>());
        var list = Assert.IsType<ListBlock>(doc.Blocks[2]);
        Assert.Equal(2, list.Items.Count);
        var nested = Assert.IsType<ListBlock>(list.Items[1][1]);
        Assert.True(nested.Ordered);
        Assert.Equal(5, nested.Start);
        Assert.IsType<QuoteBlock>(doc.Blocks[3]);
        Assert.Equal("x\n  y", Assert.IsType<CodeBlock>(doc.Blocks[4]).Text);
        Assert.IsType<RuleBlock>(doc.Blocks[5]);
    }

    [Fact]
    public void Html_whitespace_is_collapsed_and_br_breaks_lines()
    {
        var doc = NoteFormatter.Format("<div>\n   Hello   <b>big</b>\n world<br>next  </div>");
        Assert.Equal("Hello big world\nnext", doc.ToPlainText());
    }

    [Theory]
    [InlineData("before<script>alert('pwn')</script>after", "pwn")]
    [InlineData("before<iframe src=\"https://evil.example\">fallback pwn</iframe>after", "pwn")]
    [InlineData("before<style>p{color:red} pwn</style>after", "pwn")]
    [InlineData("before<object data=\"x\">pwn</object><embed src=\"x\">after", "pwn")]
    [InlineData("before<svg><script>pwn</script></svg>after", "pwn")]
    [InlineData("before<noscript>pwn</noscript>after", "pwn")]
    [InlineData("before<template>pwn</template>after", "pwn")]
    [InlineData("before<form><input value=\"pwn\"><textarea>pwn</textarea></form>after", "pwn")]
    [InlineData("<p onclick=\"pwn()\" style=\"x\">before</p><p>after</p>", "pwn")]
    [InlineData("<p>before</p><SCRIPT>pwn</SCRIPT><p>after</p>", "pwn")]
    [InlineData("<p>before</p><scr<script>ipt>pwn</script><p>after</p>", "alert")]
    public void Dangerous_html_is_dropped_with_its_content(string html, string forbidden)
    {
        var text = NoteFormatter.Format(html).ToPlainText();
        Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        Assert.Contains("before", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text <script>pwn</script> more")]
    [InlineData("text <iframe src=\"https://x\">pwn</iframe> more")]
    [InlineData("text **bold** <script>pwn</script> more")]
    [InlineData("# Head\n\n<script>\npwn\n</script>\n\nmore")]
    [InlineData("# Head\n\n<iframe src=\"https://x\"></iframe>\n\n- pwn?")]
    public void Dangerous_html_inside_markdown_is_dropped(string markdown)
    {
        var text = NoteFormatter.Format(markdown).ToPlainText();
        Assert.DoesNotContain("pwn", text.Replace("pwn?", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("iframe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("vbscript:x")]
    [InlineData("/relative")]
    [InlineData("https://google.com@evil.example/")]
    [InlineData("")]
    public void Unsafe_link_targets_are_refused(string href)
    {
        Assert.False(NoteLinks.TryCreate(href, out _));
        var html = NoteFormatter.Format($"<p><a href=\"{href.Replace("\n", "&#10;", StringComparison.Ordinal)}\">click me</a></p>");
        Assert.Empty(((ParagraphBlock)html.Blocks[0]).Inlines.OfType<LinkInline>());
        Assert.Equal("click me", html.ToPlainText());
        var markdown = NoteFormatter.Format($"**x** [click me](<{href.Replace("\n", " ", StringComparison.Ordinal)}>)");
        Assert.DoesNotContain(markdown.Blocks.OfType<ParagraphBlock>().SelectMany(p => p.Inlines), i => i is LinkInline { Target.Scheme: not ("http" or "https" or "mailto") });
    }

    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("http://example.com")]
    [InlineData("mailto:me@example.com")]
    public void Web_and_mail_links_are_accepted(string href) => Assert.True(NoteLinks.TryCreate(href, out _));

    [Fact]
    public void Images_in_html_are_never_links_and_show_alt_text()
    {
        var doc = NoteFormatter.Format("<p>a <img src=\"https://tracker.example/p.gif\" alt=\"chart\"> b <img src=\"https://tracker.example/q.gif\"></p>");
        Assert.Equal("a [chart] b", doc.ToPlainText());
    }

    [Theory]
    [InlineData("a <!-- secret --> b", "secret")]
    [InlineData("<p>a</p><!-- secret\nover lines --><p>b</p>", "secret")]
    [InlineData("<p>a<!-- secret", "secret")]
    [InlineData("<p>a<!--> b<!---> c</p>", ">")]
    [InlineData("<p>a<!-- x --!> b</p>", "x")]
    [InlineData("<p>a <?php secret ?> b</p>", "secret")]
    [InlineData("<p>a <![CDATA[secret]]> b</p>", "secret")]
    public void Html_comments_are_hidden(string html, string forbidden)
    {
        var text = NoteFormatter.Format(html).ToPlainText();
        Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        Assert.StartsWith("a", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# Title\n\n<!-- secret -->\n\nvisible")]
    [InlineData("# Title\n\nvisible <!-- secret --> text")]
    [InlineData("# Title\n\nvisible\n<!--\nsecret\n\nstill secret\n-->\ntext")]
    [InlineData("# Title\n\nvisible <!-- secret\n\nstill secret --> text")]
    [InlineData("# Title\n\nvisible <!-- secret")]
    [InlineData("# Title\n\nvisible\n\n[//]: # (secret)\n\n[comment]: <> (secret)")]
    [InlineData("# Title\n\n- item <!-- secret --> one\n- two\n\n> quote <!-- secret -->")]
    [InlineData("# Title\n\nvisible <!<!-- x -->-- secret -->")]
    [InlineData("# Title\n\n<!-- secret --> visible")]
    [InlineData("# Title\n\n**vis<!-- secret -->ible**")]
    public void Markdown_comments_are_hidden(string markdown)
    {
        var doc = NoteFormatter.Format(markdown);
        var text = doc.ToPlainText();
        Assert.Equal(NoteFormat.Markdown, doc.Format);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
        Assert.Contains("Title", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_comment_text_after_the_comment_on_the_same_line_stays()
    {
        var text = NoteFormatter.Format("# T\n\n<!-- secret --> visible").ToPlainText();
        Assert.Contains("visible", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_in_code_are_shown_because_they_are_code()
    {
        var doc = NoteFormatter.Format("# T\n\nUse `<!-- note -->` like this:\n\n```html\n<!-- in fence -->\n<p>x</p>\n```\n");
        var text = doc.ToPlainText();
        Assert.Contains("<!-- note -->", text, StringComparison.Ordinal);
        Assert.Contains("<!-- in fence -->", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_inside_pre_does_not_run_or_show_scripts()
    {
        var text = NoteFormatter.Format("<pre>a<script>pwn</script>b<!-- c --></pre>").ToPlainText();
        Assert.Equal("ab", text);
    }

    [Fact]
    public void Hostile_nesting_and_size_are_bounded()
    {
        foreach (var input in new[]
        {
            string.Concat(Enumerable.Repeat("<div>", 20_000)) + "deep",
            string.Concat(Enumerable.Repeat("<b>", 20_000)) + "deep",
            string.Concat(Enumerable.Repeat("<ul><li>", 5_000)) + "deep",
            string.Concat(Enumerable.Repeat("> ", 20_000)) + "deep",
            string.Concat(Enumerable.Repeat("- ", 20_000)) + "deep",
            string.Concat(Enumerable.Repeat("*a ", 20_000)),
            string.Concat(Enumerable.Repeat("` ", 20_000)) + "<!--",
            string.Concat(Enumerable.Repeat("[x](", 10_000)),
            string.Concat(Enumerable.Repeat("<p>x</p>", 100_000)),
            string.Concat(Enumerable.Repeat("# h\n\n", 100_000)),
            new string('a', 1_000_000),
        })
        {
            var doc = NoteFormatter.Format(input);
            Assert.True(doc.Blocks.Count <= NoteBudget.MaxBlocks + 1);
        }
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(42);
        const string alphabet = "<>/!-=\"'`*_#[]()~ \n\tabpsiv&;0123456789";
        for (var n = 0; n < 500; n++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var doc = NoteFormatter.Format(text);
            Assert.NotNull(doc.ToPlainText());
        }
    }
}
