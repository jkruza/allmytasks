namespace AllMyTasks.Providers.ICal;

/// <summary>Makes URLs safe to log: private feed links often carry their secret in the user info, path token or query.</summary>
public static class UrlRedactor
{
    /// <summary>Returns <c>scheme://host</c> only. Path, query and user info are dropped; anything unparseable becomes a placeholder.</summary>
    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "(invalid address)";

    public static string Redact(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return $"{uri.Scheme}://{uri.Host}";
    }

    /// <summary>Turns <c>webcal://</c> and <c>webcals://</c> into <c>http(s)</c>. Returns null for anything that is not a usable feed address.</summary>
    public static Uri? NormalizeFeedUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var scheme = uri.Scheme switch
        {
            "webcal" => "http",
            "webcals" => "https",
            "http" or "https" => uri.Scheme,
            _ => null,
        };
        if (scheme is null)
        {
            return null;
        }

        // webcal is conventionally served over https today; a plain "webcal://" is upgraded rather than sent in clear.
        if (uri.Scheme == "webcal")
        {
            scheme = "https";
        }

        return new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
    }
}
