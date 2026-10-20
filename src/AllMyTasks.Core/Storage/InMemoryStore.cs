using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Storage;

/// <summary>
/// A store that keeps everything in memory. The fake used by Core and view tests, and by the app
/// until the SQLite store arrives.
/// </summary>
public sealed class InMemoryStore : IItemStore, ISourceRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ItemKey, CalendarEvent> _events = [];
    private readonly Dictionary<ItemKey, TaskItem> _tasks = [];
    private readonly Dictionary<(string Source, string Collection), CollectionInfo> _collections = [];
    private readonly Dictionary<(string Source, string Collection), (bool IsVisible, string? Color)> _preferences = [];
    private readonly Dictionary<string, SourceInfo> _sources = [];

    public Task ApplyAsync(string sourceId, string collectionId, SyncResult<CalendarEvent> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            Apply(_events, sourceId, collectionId, result, e => e.Key);
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(string sourceId, string collectionId, SyncResult<TaskItem> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            Apply(_tasks, sourceId, collectionId, result, t => t.Key);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CalendarEvent>> GetEventCandidatesAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var recurring = _events.Values
                .Where(e => e.Recurrence is not null || e.Key.IsOverride)
                .Select(e => e.Key.Master)
                .ToHashSet();

            IReadOnlyList<CalendarEvent> result =
            [
                .. _events.Values.Where(e =>
                    recurring.Contains(e.Key.Master) || e.Time.Overlaps(rangeStart, rangeEnd, displayZone)),
            ];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TaskItem>>([.. _tasks.Values]);
        }
    }

    public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SourceInfo>>([.. _sources.Values]);
        }
    }

    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CollectionInfo>>([.. _collections.Values]);
        }
    }

    public Task SaveCollectionsAsync(SourceInfo source, IReadOnlyList<CollectionInfo> collections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(collections);
        lock (_gate)
        {
            _sources[source.Id] = source;
            var incoming = collections.Select(c => c.Id).ToHashSet();
            foreach (var stale in _collections.Keys.Where(k => k.Source == source.Id && !incoming.Contains(k.Collection)).ToList())
            {
                _collections.Remove(stale);
                _preferences.Remove(stale);
            }

            foreach (var collection in collections)
            {
                var key = (source.Id, collection.Id);
                _collections[key] = ApplyPreferences(key, collection);
            }
        }

        return Task.CompletedTask;
    }

    public Task SetCollectionPreferencesAsync(string sourceId, string collectionId, bool isVisible, string? colorOverride, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = (sourceId, collectionId);
            _preferences[key] = (isVisible, colorOverride);
            if (_collections.TryGetValue(key, out var existing))
            {
                _collections[key] = ApplyPreferences(key, existing);
            }
        }

        return Task.CompletedTask;
    }

    private CollectionInfo ApplyPreferences((string Source, string Collection) key, CollectionInfo collection) =>
        _preferences.TryGetValue(key, out var pref)
            ? collection with { IsVisible = pref.IsVisible, Color = pref.Color ?? collection.Color }
            : collection;

    private static void Apply<T>(
        Dictionary<ItemKey, T> items,
        string sourceId,
        string collectionId,
        SyncResult<T> result,
        Func<T, ItemKey> keyOf)
    {
        if (result.IsFullSnapshot)
        {
            foreach (var key in items.Keys.Where(k => k.SourceId == sourceId && k.CollectionId == collectionId).ToList())
            {
                items.Remove(key);
            }
        }

        foreach (var key in items.Keys.Where(k => k.SourceId == sourceId && k.CollectionId == collectionId && result.DeletedItemIds.Contains(k.ItemId)).ToList())
        {
            items.Remove(key);
        }

        foreach (var item in result.Upserts)
        {
            items[keyOf(item)] = item;
        }
    }
}
