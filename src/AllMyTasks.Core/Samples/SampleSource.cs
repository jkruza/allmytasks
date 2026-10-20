using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Samples;

/// <summary>
/// A made-up source with two calendars and two task lists, regenerated relative to "today" so the sample data
/// always looks current. Serves as the app's first-run content until real providers are added.
/// </summary>
public sealed class SampleSource(string id, string displayName, TimeProvider clock, TimeZoneInfo zone) : ICalendarSource, ITaskSource
{
    public string Id { get; } = Guard.NotBlank(id);

    public string Kind => SampleSourceFactory.KindName;

    public string DisplayName { get; } = Guard.NotNull(displayName);

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var today = Today();
        var all = new List<CollectionInfo>();
        foreach (var inner in Parts(today))
        {
            all.AddRange(await inner.GetCollectionsAsync(cancellationToken).ConfigureAwait(false));
        }

        return all;
    }

    public async Task<SyncResult<CalendarEvent>> SyncEventsAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var today = Today();
        var state = new SyncState("sample:" + today.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (previous?.Token == state.Token)
        {
            return SyncResult.Delta<CalendarEvent>([], [], state);
        }

        foreach (var inner in Parts(today).OfType<InMemoryCalendarSource>())
        {
            if ((await inner.GetCollectionsAsync(cancellationToken).ConfigureAwait(false)).Any(c => c.Id == collection.Id))
            {
                var result = await inner.SyncEventsAsync(collection, null, cancellationToken).ConfigureAwait(false);
                return SyncResult.Snapshot(result.Upserts, state);
            }
        }

        return SyncResult.Snapshot<CalendarEvent>([], state, confirmedEmpty: true);
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var today = Today();
        var state = new SyncState("sample:" + today.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (previous?.Token == state.Token)
        {
            return SyncResult.Delta<TaskItem>([], [], state);
        }

        foreach (var inner in Parts(today).OfType<InMemoryTaskSource>())
        {
            if ((await inner.GetCollectionsAsync(cancellationToken).ConfigureAwait(false)).Any(c => c.Id == collection.Id))
            {
                var result = await inner.SyncTasksAsync(collection, null, cancellationToken).ConfigureAwait(false);
                return SyncResult.Snapshot(result.Upserts, state);
            }
        }

        return SyncResult.Snapshot<TaskItem>([], state, confirmedEmpty: true);
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);

    private List<ISource> Parts(DateOnly today) =>
    [
        SampleData.WorkCalendar(today, zone, Id),
        SampleData.PersonalCalendar(today, zone, Id),
        SampleData.WorkTasks(today, zone, Id),
        SampleData.HomeTasks(today, zone, Id),
    ];
}

/// <summary>Creates <see cref="SampleSource"/>s. Needs no settings.</summary>
public sealed class SampleSourceFactory(TimeProvider clock, TimeZoneInfo zone) : ISourceFactory
{
    public const string KindName = "sample";

    public string Kind => KindName;

    public string DisplayName => "Sample data";

    public SourceConfigSchema ConfigSchema => SourceConfigSchema.Empty;

    public ISource Create(string sourceId, string displayName, SourceConfig config, ISecretStore secrets) =>
        new SampleSource(sourceId, displayName, clock, zone);
}
