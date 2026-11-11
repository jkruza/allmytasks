namespace AllMyTasks.Core.About;

/// <summary>Whether a build carries a built-in OAuth client. Only this state is ever shown, never the id or secret.</summary>
public enum ClientStatus
{
    NotIncluded,

    /// <summary>Only one of client id and secret is present, so sign-in with the built-in client would fail.</summary>
    Incomplete,

    Included,
}

/// <summary>Works out a <see cref="ClientStatus"/> from the raw build values without keeping them.</summary>
public static class ClientStatusRules
{
    public static ClientStatus From(string? clientId, string? clientSecret, bool requiresSecret)
    {
        var hasId = !string.IsNullOrWhiteSpace(clientId);
        if (!requiresSecret)
        {
            return hasId ? ClientStatus.Included : ClientStatus.NotIncluded;
        }

        var hasSecret = !string.IsNullOrWhiteSpace(clientSecret);
        return (hasId, hasSecret) switch
        {
            (true, true) => ClientStatus.Included,
            (false, false) => ClientStatus.NotIncluded,
            _ => ClientStatus.Incomplete,
        };
    }
}

/// <summary>Implemented by source factories that can ship a built-in OAuth client; exposes the state only.</summary>
public interface IBuiltInClientInfo
{
    /// <summary>Provider name for the About dialog, such as "Google".</summary>
    string ClientProvider { get; }

    ClientStatus BuiltInClientStatus { get; }
}
