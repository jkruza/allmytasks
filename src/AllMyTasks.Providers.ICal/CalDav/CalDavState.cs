using System.Text.Json;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.ICal.CalDav;

/// <summary>What the source knows about one resource, so removals (which only name a href) can be turned into item ids.</summary>
/// <param name="ETag">The resource's entity tag at the last sync.</param>
/// <param name="Uids">The event UIDs the resource held.</param>
internal sealed record ResourceRef(string ETag, string[] Uids);

/// <summary>
/// The opaque <see cref="SyncState"/> of a CalDAV collection: which strategy produced it, the server's token or ctag, and
/// the href map. Only this provider reads it.
/// </summary>
/// <param name="Mode"><c>sync</c> (RFC 6578 token), <c>ctag</c> (ctag + etag diff) or <c>query</c> (full query).</param>
/// <param name="Token">The <c>sync-token</c> in sync mode.</param>
/// <param name="CTag">The collection's <c>getctag</c> in ctag mode.</param>
/// <param name="Items">href to etag and UIDs.</param>
internal sealed record CalDavState(string Mode, string? Token, string? CTag, Dictionary<string, ResourceRef> Items)
{
    public SyncState ToSyncState() => new(JsonSerializer.Serialize(this));

    public static CalDavState? Parse(SyncState? state)
    {
        if (state is null)
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CalDavState>(state.Token);
            return parsed is { Items: not null } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
