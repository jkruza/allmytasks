using AllMyTasks.Core.Search;

namespace AllMyTasks.Core.Tests;

public class TextMatcherTests
{
    [Theory]
    [InlineData("rep", "Write report", true)]
    [InlineData("REPORT", "write report", true)]
    [InlineData("port", "Write report", false)]
    [InlineData("write rep", "Write report", true)]
    [InlineData("rep write", "Write report", true)]
    [InlineData("write mail", "Write report", false)]
    [InlineData("", "anything", true)]
    [InlineData("   ", "anything", true)]
    [InlineData("zolc", "Żółć", true)]
    [InlineData("ZOLC", "żółć", true)]
    [InlineData("zażółć", "Zażółć gęślą jaźń", true)]
    [InlineData("gesla", "Zażółć gęślą jaźń", true)]
    [InlineData("strasse", "Straße 5", true)]
    [InlineData("cafe", "Café Müller", true)]
    [InlineData("muller", "Café Müller", true)]
    [InlineData("q3", "Review Q3-report", true)]
    [InlineData("q3 report", "Review Q3-report", true)]
    public void Words_match_the_start_of_a_word_ignoring_case_and_accents(string query, string field, bool expected) =>
        Assert.Equal(expected, TextMatcher.Matches(query, field));

    [Theory]
    [InlineData("\"weekly sync\"", "Weekly sync with team", true)]
    [InlineData("\"weekly sy\"", "Weekly sync with team", true)]
    [InlineData("\"sync weekly\"", "Weekly sync with team", false)]
    [InlineData("\"eekly sync\"", "Weekly sync with team", false)]
    [InlineData("\"weekly team\"", "Weekly sync with team", false)]
    [InlineData("\"weekly sync", "Weekly sync with team", true)]
    [InlineData("\"\"", "Weekly sync with team", true)]
    public void A_quoted_phrase_must_match_as_a_whole(string query, string field, bool expected) =>
        Assert.Equal(expected, TextMatcher.Matches(query, field));

    [Fact]
    public void A_phrase_and_words_combine()
    {
        Assert.True(TextMatcher.Matches("\"weekly sync\" team", "Weekly sync with team"));
        Assert.False(TextMatcher.Matches("\"weekly sync\" budget", "Weekly sync with team"));
    }

    [Fact]
    public void Each_word_may_match_a_different_field_but_a_phrase_stays_in_one()
    {
        Assert.True(TextMatcher.Matches("budget room", "Budget review", null, "Meeting room 4"));
        Assert.False(TextMatcher.Matches("\"review meeting\"", "Budget review", "Meeting room 4"));
    }

    [Fact]
    public void Missing_fields_never_match_but_do_not_fail()
    {
        Assert.False(TextMatcher.Matches("x", null, string.Empty, "  "));
        Assert.True(TextMatcher.Matches(null));
    }

    [Fact]
    public void Normalize_folds_case_accents_and_special_letters()
    {
        Assert.Equal("zolc", TextMatcher.Normalize("ŻÓŁĆ"));
        Assert.Equal("strasse", TextMatcher.Normalize("Straße"));
        Assert.Equal(string.Empty, TextMatcher.Normalize(null));
    }
}
