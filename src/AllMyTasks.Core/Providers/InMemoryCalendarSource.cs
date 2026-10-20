using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// A calendar source backed by a fixed list. Used for samples and tests until real providers exist.
/// </summary>
public sealed class InMemoryCalendarSource(
    string id,
    string displayName,
    IEnumerable<CollectionInfo> collections,
    IEnumerable<CalendarEvent> events) : ICalendarSource
{
    private static readonly string StateToken = "in-memory";

    private readonly IReadOnlyList<CollectionInfo> _collections = [.. collections];
    private readonly IReadOnlyList<CalendarEvent> _events = [.. events];

    public string Id { get; } = Guard.NotBlank(id);

    public string Kind => "in-memory";

    public string DisplayName { get; } = Guard.NotNull(displayName);

    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_collections);

    /// <summary>
    /// A first sync returns everything in the collection as a snapshot. Syncing again with the state
    /// from the previous result returns an empty delta. Any other state counts as expired.
    /// </summary>
    public Task<SyncResult<CalendarEvent>> SyncEventsAsync(
        CollectionInfo collection,
        SyncState? previous,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var state = new SyncState(StateToken);
        if (previous is null)
        {
            var items = _events
                .Where(e => e.Key.SourceId == collection.SourceId && e.Key.CollectionId == collection.Id)
                .ToList();
            return Task.FromResult(SyncResult.Snapshot(items, state));
        }

        return previous.Token == StateToken
            ? Task.FromResult(SyncResult.Delta<CalendarEvent>([], [], state))
            : Task.FromException<SyncResult<CalendarEvent>>(new SyncStateExpiredException("Unknown sync state."));
    }
}
