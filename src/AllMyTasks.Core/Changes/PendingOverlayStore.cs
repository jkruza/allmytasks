using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Changes;

/// <summary>
/// The item store that views read from: the cache with <see cref="PendingOverlay"/> applied to its tasks. The sync
/// engine writes to the plain store instead, so the overlay never leaks into the cache.
/// </summary>
public sealed class PendingOverlayStore(IItemStore inner, IPendingChangeStore pending) : IItemStore
{
    public Task ApplyAsync(string sourceId, string collectionId, SyncResult<CalendarEvent> result, CancellationToken cancellationToken = default) =>
        inner.ApplyAsync(sourceId, collectionId, result, cancellationToken);

    public Task ApplyAsync(string sourceId, string collectionId, SyncResult<TaskItem> result, CancellationToken cancellationToken = default) =>
        inner.ApplyAsync(sourceId, collectionId, result, cancellationToken);

    public Task<IReadOnlyList<CalendarEvent>> GetEventCandidatesAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone,
        CancellationToken cancellationToken = default) =>
        inner.GetEventCandidatesAsync(rangeStart, rangeEnd, displayZone, cancellationToken);

    public Task<int> CountItemsAsync(string sourceId, string collectionId, CancellationToken cancellationToken = default) =>
        inner.CountItemsAsync(sourceId, collectionId, cancellationToken);

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await inner.GetTasksAsync(cancellationToken).ConfigureAwait(false);
        var changes = await pending.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return PendingOverlay.Apply(tasks, changes);
    }
}
