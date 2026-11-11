using System.Text;

namespace AllMyTasks.Core.About;

/// <summary>Fixed project links shown in the About dialog.</summary>
public static class ProjectLinks
{
    public const string Website = "https://allmytasks.jkruza.com";
    public const string Repository = "https://github.com/jkruza/allmytasks";
    public const string Issues = "https://github.com/jkruza/allmytasks/issues";
}

/// <summary>One provider's line in the About dialog.</summary>
public sealed record ClientLine(string Provider, ClientStatus Status)
{
    public string StatusText => Status switch
    {
        ClientStatus.Included => "Included",
        ClientStatus.Incomplete => "Incomplete",
        _ => "Not included",
    };

    public string Consequence => Status switch
    {
        ClientStatus.Included => "Adding a source just opens the browser sign-in.",
        ClientStatus.Incomplete => "Only part of the client is built in; enter your own client when adding a source.",
        _ => "Enter your own client when adding a source (docs/oauth-setup.md).",
    };
}

/// <summary>What the About dialog shows. Holds no client values, so nothing here can leak one.</summary>
public sealed record AboutInfo(string Version, string? Commit, string Runtime, string OperatingSystem, IReadOnlyList<ClientLine> Clients)
{
    public static AboutInfo Create(string version, string? commit, string runtime, string operatingSystem, IEnumerable<IBuiltInClientInfo> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        return new AboutInfo(
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            string.IsNullOrWhiteSpace(commit) ? null : commit,
            runtime,
            operatingSystem,
            [.. clients.Select(c => new ClientLine(c.ClientProvider, c.BuiltInClientStatus)).OrderBy(c => c.Provider, StringComparer.Ordinal)]);
    }

    /// <summary>Splits an informational version such as "0.9.0-alpha+abc1234def" into version and short commit.</summary>
    public static (string Version, string? Commit) SplitVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("unknown", null);
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, null);
        }

        var commit = informational[(plus + 1)..];
        return (informational[..plus], commit.Length == 0 ? null : commit[..Math.Min(commit.Length, 9)]);
    }

    /// <summary>Plain text for bug reports.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("All My Tasks ").AppendLine(Version);
        if (Commit is not null)
        {
            text.Append("Commit: ").AppendLine(Commit);
        }

        text.Append("Runtime: ").AppendLine(Runtime);
        text.Append("OS: ").AppendLine(OperatingSystem);
        foreach (var client in Clients)
        {
            text.Append("Built-in ").Append(client.Provider).Append(" client: ").AppendLine(client.StatusText);
        }

        return text.ToString();
    }
}
