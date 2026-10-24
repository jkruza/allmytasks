using AllMyTasks.Core.Providers;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Core.Tests;

internal sealed class ListLogger : ILogger
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception));
}

public class DiagnosticTextTests
{
    [Fact]
    public void Addresses_are_removed_and_whitespace_is_flattened()
    {
        var text = DiagnosticText.Scrub("failed for https://user:pw@example.com/a/SECRET?x=1 and webcal://example.com/b\r\n\tdone");

        Assert.Equal("failed for <url> and <url> done", text);
    }

    [Fact]
    public void Long_text_is_truncated()
    {
        var text = DiagnosticText.Scrub(new string('x', 500), 50);

        Assert.Equal(51, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_names_the_exception_chain_and_leaves_the_message_out_on_request()
    {
        var ex = new InvalidOperationException("token=xyz", new FormatException("inner detail"));

        var without = DiagnosticText.Describe(ex, includeMessage: false);
        var with = DiagnosticText.Describe(ex, includeMessage: true);

        Assert.Contains("System.InvalidOperationException", without, StringComparison.Ordinal);
        Assert.Contains("caused by System.FormatException", without, StringComparison.Ordinal);
        Assert.DoesNotContain("token=xyz", without, StringComparison.Ordinal);
        Assert.Contains("token=xyz", with, StringComparison.Ordinal);
        Assert.Contains("inner detail", with, StringComparison.Ordinal);
    }
}
