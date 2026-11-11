namespace AllMyTasks.Core.Notes;

/// <summary>Decides which link targets in a note may be offered to the user.</summary>
public static class NoteLinks
{
    /// <summary>Only web and mail links. <c>javascript:</c>, <c>data:</c>, <c>file:</c>, custom schemes and relative links are refused.</summary>
    public static bool TryCreate(string? href, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var text = href.Trim();
        if (text.Any(char.IsControl) || text.Length > 2_000)
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        // https://google.com@evil.example/ is a classic disguise; the same rule as for meeting links.
        if (parsed.Scheme != "mailto" && parsed.UserInfo.Length > 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
