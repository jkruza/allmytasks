using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// A task source backed by a fixed list. Used for samples and tests until real providers exist.
/// </summary>
public sealed class InMemoryTaskSource(
    string id,
    string displayName,
    IEnumerable<CollectionInfo> collections,
    IEnumerable<TaskItem> tasks) : ITaskSource
{
    private static readonly string StateToken = "in-memory";

    private readonly IReadOnlyList<CollectionInfo> _collections = [.. collections];
    private readonly IReadOnlyList<TaskItem> _tasks = [.. tasks];

    public string Id { get; } = Guard.NotBlank(id);

    public string Kind => "in-memory";

    public string DisplayName { get; } = Guard.NotNull(displayName);

    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_collections);

    /// <summary>
    /// A first sync returns everything in the collection as a snapshot. Syncing again with the state
    /// from the previous result returns an empty delta. Any other state counts as expired.
    /// </summary>
    public Task<SyncResult<TaskItem>> SyncTasksAsync(
        CollectionInfo collection,
        SyncState? previous,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var state = new SyncState(StateToken);
        if (previous is null)
        {
            var items = _tasks.Where(t => IsIn(t.Key, collection)).ToList();
            return Task.FromResult(SyncResult.Snapshot(items, state));
        }

        return previous.Token == StateToken
            ? Task.FromResult(SyncResult.Delta<TaskItem>([], [], state))
            : Task.FromException<SyncResult<TaskItem>>(new SyncStateExpiredException("Unknown sync state."));
    }

    private static bool IsIn(ItemKey key, CollectionInfo collection) =>
        key.SourceId == collection.SourceId && key.CollectionId == collection.Id;
}
