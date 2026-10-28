using System.Text.RegularExpressions;

namespace AllMyTasks.Core.Calendar;

/// <summary>
/// Validates and names online meeting links. Links come from third parties, so only plain <c>https</c> addresses without
/// credentials are accepted.
/// </summary>
public static partial class MeetingLinks
{
    /// <summary>Longest link kept.</summary>
    public const int MaxLength = 2048;

    private static readonly (string Suffix, string Name)[] Services =
    [
        ("meet.google.com", "Google Meet"),
        ("teams.microsoft.com", "Microsoft Teams"),
        ("teams.live.com", "Microsoft Teams"),
        ("zoom.us", "Zoom"),
        ("zoom.com", "Zoom"),
        ("zoomgov.com", "Zoom"),
        ("webex.com", "Webex"),
        ("whereby.com", "Whereby"),
        ("gotomeeting.com", "GoToMeeting"),
        ("meet.jit.si", "Jitsi Meet"),
    ];

    /// <summary>An absolute <c>https</c> URL with a host, no credentials and at most <see cref="MaxLength"/> characters, or null.</summary>
    public static Uri? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return null;
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && IsSafe(uri) ? uri : null;
    }

    /// <summary>True for an absolute <c>https</c> URL with a host and no credentials.</summary>
    public static bool IsSafe(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0 }
        && !string.IsNullOrEmpty(uri.Host)
        && uri.OriginalString.Length <= MaxLength;

    /// <summary>The service for a known meeting host (<c>meet.google.com</c> gives "Google Meet"), or null for any other host.</summary>
    public static string? ServiceName(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var (suffix, name) in Services)
        {
            if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>True when the link is on a known meeting host.</summary>
    public static bool IsKnownHost(Uri? uri) => ServiceName(uri) is not null;

    /// <summary>The text of the join button: "Join Google Meet", or "Join meeting" for a host that is not known.</summary>
    public static string JoinLabel(Uri uri) => ServiceName(uri) is { } name ? $"Join {name}" : "Join meeting";

    /// <summary>
    /// The first link in free text that is safe and on a known meeting host. An arbitrary link in a description is not a meeting
    /// link, so unknown hosts are ignored.
    /// </summary>
    public static Uri? FindInText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Feeds can hold very large values; links near the start are the ones that matter.
        var window = text.Length > 50_000 ? text[..50_000] : text;
        foreach (Match match in UrlPattern().Matches(window))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', '>', '"', '\'');
            if (Sanitize(candidate) is { } uri && IsKnownHost(uri))
            {
                return uri;
            }
        }

        return null;
    }

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlPattern();
}
