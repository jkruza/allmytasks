using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Views;
using AllMyTasks.Services;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>One row of the source status list.</summary>
public sealed partial class SourceItemViewModel : ObservableObject
{
    private readonly SourcesViewModel _owner;

    public SourceItemViewModel(SourceInfo info, string kindName, SourcesViewModel owner)
    {
        Info = info;
        KindName = kindName;
        _owner = owner;
        Name = info.DisplayName;
    }

    public SourceInfo Info { get; }

    public string KindName { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = Brushes.Gray;

    public ObservableCollection<CollectionRowViewModel> Collections { get; } = [];

    public bool HasCollections => Collections.Count > 0;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool NeedsAttention { get; set; }

    [RelayCommand]
    private Task RefreshAsync() => _owner.RefreshAsync(this);

    [RelayCommand]
    private Task EditAsync() => _owner.EditAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    public void Apply(SourceStatus status, DateTimeOffset now)
    {
        IsBusy = status.Kind == SourceStatusKind.Syncing;
        NeedsAttention = status.Kind == SourceStatusKind.NeedsAttention;
        (StatusText, StatusBrush) = status.Kind switch
        {
            SourceStatusKind.New => ("Not synced yet", Brushes.Gray),
            SourceStatusKind.Ok => ("Up to date", Brushes.SeaGreen),
            SourceStatusKind.Syncing => ("Syncing…", Brushes.SteelBlue),
            SourceStatusKind.Offline => ("Offline", Brushes.DarkOrange),
            SourceStatusKind.Error => ("Error", Brushes.IndianRed),
            _ => ("Needs attention", Brushes.IndianRed),
        };

        var parts = new List<string>();
        parts.Add(status.LastSyncedAt is { } last ? $"Last synced {RelativeTime.Ago(last, now)}" : "Never synced");
        if (status.LastError is { Length: > 0 } error)
        {
            parts.Add(error + (status.Kind == SourceStatusKind.NeedsAttention ? " Edit the source to fix it." : string.Empty));
        }

        if (status.NextRetryAt is { } retry && status.Kind is SourceStatusKind.Offline or SourceStatusKind.Error)
        {
            parts.Add($"Retrying {(retry > now ? "in " + Math.Max(1, (int)Math.Ceiling((retry - now).TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min" : "soon")}");
        }

        if (status.SkippedItems > 0)
        {
            parts.Add($"{status.SkippedItems} item{(status.SkippedItems == 1 ? string.Empty : "s")} skipped");
        }

        if (status.Warning is { Length: > 0 } warning)
        {
            parts.Add(warning);
        }

        Detail = string.Join(" · ", parts);
    }
}

/// <summary>The "Sources" window: per-source status, refresh, edit, remove and add.</summary>
public sealed partial class SourcesViewModel : ViewModelBase, IDisposable
{
    private readonly SyncEngine _engine;
    private readonly SourceRegistry _registry;
    private readonly ISourceRepository _repository;
    private readonly TimeProvider _clock;
    private readonly DialogService _dialogs;
    private readonly ITimer _ticker;
    private readonly Func<Task>? _collectionsChanged;
    private readonly Dictionary<string, SourceStatus> _latest = [];

    public SourcesViewModel(
        SyncEngine engine,
        SourceRegistry registry,
        ISourceRepository repository,
        TimeProvider clock,
        DialogService dialogs,
        Func<Task>? collectionsChanged = null)
    {
        _collectionsChanged = collectionsChanged;
        _engine = engine;
        _registry = registry;
        _repository = repository;
        _clock = clock;
        _dialogs = dialogs;
        _engine.StatusChanged += OnStatusChanged;
        // Keeps "5 minutes ago" honest while the window is open.
        _ticker = clock.CreateTimer(_ => Dispatcher.UIThread.Post(RefreshTexts), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        var infos = await _repository.GetSourcesAsync();
        var statuses = (await _engine.GetStatusesAsync()).ToDictionary(s => s.SourceId);
        Sources.Clear();
        _latest.Clear();
        foreach (var (id, status) in statuses)
        {
            _latest[id] = status;
        }

        var collections = await _repository.GetCollectionsAsync();
        foreach (var info in infos)
        {
            var item = new SourceItemViewModel(info, _registry.FactoryFor(info.Kind)?.DisplayName ?? info.Kind, this);
            foreach (var c in collections.Where(c => c.SourceId == info.Id))
            {
                item.Collections.Add(new CollectionRowViewModel(c, null, OnCollectionChangedAsync));
            }

            item.Apply(statuses.GetValueOrDefault(info.Id) ?? SourceStatus.NewFor(info.Id), _clock.GetUtcNow());
            Sources.Add(item);
        }

        IsEmpty = Sources.Count == 0;
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        try
        {
            await _engine.SyncAllAsync();
        }
        catch (OperationCanceledException)
        {
            // Not cancelled by us; nothing to do.
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var editor = new SourceEditorViewModel(_registry, null, _repository);
        if (await _dialogs.ShowSourceEditorAsync(editor))
        {
            await LoadAsync();
            if (editor.SavedSourceId is { } id)
            {
                _ = _engine.SyncSourceAsync(id);
            }
        }
    }

    /// <summary>Saves the user's visibility and colour choice, tells the views, and syncs a collection that was just shown.</summary>
    internal async Task OnCollectionChangedAsync(CollectionRowViewModel row)
    {
        await _repository.SetCollectionPreferencesAsync(row.Info.SourceId, row.Info.Id, row.IsVisible, row.ColorOverride);
        if (_collectionsChanged is not null)
        {
            await _collectionsChanged();
        }

        if (row.IsVisible && !row.Info.IsVisible)
        {
            // It was hidden, so it was not being synced: fetch it now.
            _ = _engine.SyncSourceAsync(row.Info.SourceId);
        }
    }

    internal Task RefreshAsync(SourceItemViewModel item) => _engine.SyncSourceAsync(item.Info.Id);

    internal async Task EditAsync(SourceItemViewModel item)
    {
        var editor = new SourceEditorViewModel(_registry, item.Info, _repository);
        await editor.LoadAsync();
        if (await _dialogs.ShowSourceEditorAsync(editor))
        {
            // Editing is how the user fixes rejected credentials, so the source gets another go straight away.
            await _engine.ResetAttentionAsync(item.Info.Id);
            await LoadAsync();
            _ = _engine.SyncSourceAsync(item.Info.Id);
        }
    }

    internal async Task RemoveAsync(SourceItemViewModel item)
    {
        var sure = await _dialogs.ConfirmAsync(
            $"Remove “{item.Name}”?",
            "Its cached events and tasks, and any stored passwords for it, are deleted from this computer. Nothing is changed at the provider.",
            "Remove",
            "Cancel");
        if (!sure)
        {
            return;
        }

        await _registry.RemoveAsync(item.Info.Id);
        _engine.Forget(item.Info.Id);
        await LoadAsync();
        // The cache changed, so the open views need to re-query.
        await _engine.SyncAllAsync();
    }

    public void Dispose()
    {
        _engine.StatusChanged -= OnStatusChanged;
        _ticker.Dispose();
    }

    private void OnStatusChanged(object? sender, SourceStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            _latest[status.SourceId] = status;
            var item = Sources.FirstOrDefault(s => s.Info.Id == status.SourceId);
            item?.Apply(status, _clock.GetUtcNow());
        });

    private void RefreshTexts()
    {
        foreach (var item in Sources)
        {
            if (_latest.TryGetValue(item.Info.Id, out var status))
            {
                item.Apply(status, _clock.GetUtcNow());
            }
        }
    }
}
