using System.Text;

namespace AllMyTasks.Core.Reminders;

/// <summary>
/// Notification text comes from third-party calendars, so it is treated as plain text: control characters and
/// line breaks become spaces, runs of white space collapse, and the result is cut to a fixed length.
/// </summary>
public static class ReminderText
{
    public const int MaxTitleLength = 100;
    public const int MaxDetailLength = 200;

    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length + rune.Utf16SequenceLength > maxLength)
            {
                return builder.ToString().TrimEnd() + "…";
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
