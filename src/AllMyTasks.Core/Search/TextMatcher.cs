using System.Globalization;
using System.Text;

namespace AllMyTasks.Core.Search;

/// <summary>
/// A search text split into words and quoted phrases. Every term must match (AND). See
/// docs/design/search-and-saved-filters.md#matching.
/// </summary>
public sealed class TextQuery
{
    private readonly List<string> _words = [];
    private readonly List<string> _phrases = [];

    private TextQuery()
    {
    }

    /// <summary>True when the text had no words, so everything matches.</summary>
    public bool IsEmpty => _words.Count == 0 && _phrases.Count == 0;

    /// <summary>Splits <paramref name="text"/> on whitespace; a quoted run is one phrase. An unclosed quote runs to the end.</summary>
    public static TextQuery Parse(string? text)
    {
        var query = new TextQuery();
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                query.Add(current, inQuotes);
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                query.Add(current, phrase: false);
            }
            else
            {
                current.Append(c);
            }
        }

        query.Add(current, inQuotes);
        return query;
    }

    /// <summary>
    /// Whether every term matches some field. A word matches when it is a prefix of a word in a field; a phrase
    /// matches when its words appear next to each other, starting at a word, in a single field.
    /// </summary>
    public bool Matches(IEnumerable<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (IsEmpty)
        {
            return true;
        }

        var normalised = new List<string>();
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                normalised.Add(TextMatcher.WordsOf(field));
            }
        }

        return _words.All(w => normalised.Any(f => f.Contains(" " + w, StringComparison.Ordinal)))
            && _phrases.All(p => normalised.Any(f => f.Contains(" " + p, StringComparison.Ordinal)));
    }

    private void Add(StringBuilder current, bool phrase)
    {
        var text = current.ToString();
        current.Clear();
        var words = TextMatcher.WordsOf(text).Trim();
        if (words.Length == 0)
        {
            return;
        }

        if (phrase)
        {
            _phrases.Add(words);
        }
        else
        {
            // Punctuation inside an unquoted word ("q3-report") makes it several words that must all match.
            _words.AddRange(words.Split(' '));
        }
    }
}

/// <summary>
/// The matching rule shared by global search and the All tasks text filter: case and diacritics are ignored and
/// every query word must be the start of a word in one of the fields.
/// </summary>
public static class TextMatcher
{
    /// <summary>Convenience for one-off checks; parse once with <see cref="TextQuery.Parse"/> when matching many items.</summary>
    public static bool Matches(string? query, params IEnumerable<string?> fields) =>
        TextQuery.Parse(query).Matches(fields);

    /// <summary>
    /// Folds <paramref name="text"/> for comparison: Unicode form D with combining marks removed, lower case, and the
    /// letters that do not decompose (ł, ø, đ, ß, æ, œ) mapped to their usual plain spelling.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            builder.Append(lower switch
            {
                'ł' => "l",
                'ø' => "o",
                'đ' => "d",
                'ð' => "d",
                'þ' => "th",
                'ß' => "ss",
                'æ' => "ae",
                'œ' => "oe",
                _ => lower.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// The normalised words of <paramref name="text"/>, each preceded by one space (" report q3"), so a prefix test is
    /// a plain <c>Contains(" " + prefix)</c>.
    /// </summary>
    internal static string WordsOf(string text)
    {
        var normalised = Normalize(text);
        var builder = new StringBuilder(normalised.Length + 8);
        var inWord = false;
        foreach (var c in normalised)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    builder.Append(' ');
                    inWord = true;
                }

                builder.Append(c);
            }
            else
            {
                inWord = false;
            }
        }

        return builder.ToString();
    }
}
