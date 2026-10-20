using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Tests;

/// <summary>A task source whose behaviour each test scripts.</summary>
internal sealed class ScriptedSource(string id, params CollectionInfo[] collections) : ISource, ICalendarSource, ITaskSource
{
    public string Id { get; } = id;

    public string Kind => "scripted";

    public string DisplayName => "Scripted";

    public List<SyncState?> TaskCalls { get; } = [];

    public int Calls => TaskCalls.Count;

    public Func<CollectionInfo, SyncState?, Task<SyncResult<TaskItem>>>? OnTasks { get; set; }

    public Func<CollectionInfo, SyncState?, Task<SyncResult<CalendarEvent>>>? OnEvents { get; set; }

    public Func<Task>? OnCollections { get; set; }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        if (OnCollections is not null)
        {
            await OnCollections();
        }

        return collections;
    }

    public Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        lock (TaskCalls)
        {
            TaskCalls.Add(previous);
        }

        return OnTasks!(collection, previous);
    }

    public Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default) =>
        OnEvents!(collection, previous);
}

internal sealed class FakeProvider : ISourceProvider
{
    public Dictionary<string, ISource> Sources { get; } = [];

    public Task<ISource> GetAsync(SourceInfo info, CancellationToken cancellationToken = default) =>
        Sources.TryGetValue(info.Id, out var s) ? Task.FromResult(s) : throw new SourceException($"No provider for {info.Kind}.");

    public void Invalidate(string sourceId)
    {
    }
}

internal sealed class SyncRig : IDisposable
{
    public SyncRig(int maxParallel = 4)
    {
        Store = new InMemoryStore();
        Clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        Provider = new FakeProvider();
        Engine = new SyncEngine(Store, Store, Provider, Clock, new SyncOptions(maxParallel));
    }

    public InMemoryStore Store { get; }

    public FixedClock Clock { get; }

    public FakeProvider Provider { get; }

    public SyncEngine Engine { get; }

    public static CollectionInfo List(string source, string id = "list") => new(source, id, "List " + id, CollectionKind.TaskList);

    public static TaskItem Item(string source, string id, string collection = "list") =>
        new(new ItemKey(source, collection, id), "Task " + id);

    public async Task<ScriptedSource> AddAsync(string id = "src", params CollectionInfo[] collections)
    {
        var source = new ScriptedSource(id, collections.Length == 0 ? [List(id)] : collections);
        Provider.Sources[id] = source;
        await Store.SaveSourceAsync(new SourceInfo(id, "scripted", "Source " + id), SourceConfig.Empty, TestContext.Current.CancellationToken);
        return source;
    }

    public async Task<SourceStatus> StatusAsync(string id) =>
        (await Engine.GetStatusesAsync(TestContext.Current.CancellationToken)).Single(s => s.SourceId == id);

    public void Dispose() => Engine.Dispose();
}
