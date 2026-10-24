using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Views;
using AllMyTasks.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>
/// Owns the current date, the view mode, the shared filter and the refresh command, and tells the active
/// content view model to reload whenever one of them changes.
/// </summary>
public sealed partial class CalendarShellViewModel : ViewModelBase, IDisposable
{
    private readonly ISourceRepository _sources;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly DayViewModel _day;
    private readonly WeekViewModel _week;
    private readonly MonthViewModel _month;
    private readonly UpcomingViewModel _upcoming;
    private readonly TasksViewModel _tasks;
    private readonly ITimer _midnightTimer;
    private readonly ViewStateStore? _state;
    private readonly SyncEngine? _engine;
    private readonly Action? _openSources;
    private readonly Action? _openSettings;
    private readonly SettingsService? _settings;
    private readonly Dictionary<string, SourceStatus> _statuses = [];
    private int _reloadScheduled;
    private bool _restoring;
    private CancellationTokenSource? _loading;
    private DateOnly _today;

    public CalendarShellViewModel(
        IAgendaService agenda,
        ITaskListService taskList,
        ISourceRepository sources,
        TimeProvider clock,
        TimeZoneInfo zone,
        ViewStateStore? state = null,
        SyncEngine? engine = null,
        Action? openSources = null,
        SettingsService? settings = null,
        Action? openSettings = null)
    {
        _state = state;
        _engine = engine;
        _openSources = openSources;
        _openSettings = openSettings;
        _settings = settings;
        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
        }

        if (engine is not null)
        {
            engine.SourceSynced += OnSourceSynced;
            engine.StatusChanged += OnStatusChanged;
        }

        _sources = sources;
        _clock = clock;
        _zone = zone;
        _today = Today();
        CurrentDate = _today;
        _day = new DayViewModel(agenda);
        _week = new WeekViewModel(agenda);
        _month = new MonthViewModel(agenda, OpenDay);
        _upcoming = new UpcomingViewModel(agenda);
        _tasks = new TasksViewModel(taskList, clock, state);
        Content = _day;

        // Rolls the "today" boundary over at midnight without the user having to refresh.
        _midnightTimer = clock.CreateTimer(_ => OnTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public ObservableCollection<CollectionToggle> Collections { get; } = [];

    [ObservableProperty]
    public partial ViewMode Mode { get; set; } = ViewMode.Day;

    [ObservableProperty]
    public partial DateOnly CurrentDate { get; set; }

    [ObservableProperty]
    public partial ContentViewModel Content { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowEvents { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowTasks { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowCompleted { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string LastRefreshed { get; set; } = string.Empty;

    /// <summary>One line about all sources, such as "All sources up to date" or "1 source needs attention".</summary>
    [ObservableProperty]
    public partial string SyncSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    public bool IsDay => Mode == ViewMode.Day;

    public bool IsWeek => Mode == ViewMode.Week;

    public bool IsMonth => Mode == ViewMode.Month;

    public bool IsUpcoming => Mode == ViewMode.Upcoming;

    public bool IsTasks => Mode == ViewMode.Tasks;

    /// <summary>Whether previous/next make sense for the current view.</summary>
    public bool CanNavigate => Mode is ViewMode.Day or ViewMode.Week or ViewMode.Month;

    /// <summary>Week view: hide Saturday and Sunday.</summary>
    public bool HideWeekends
    {
        get => _week.HideWeekends;
        set
        {
            _week.HideWeekends = value;
            OnPropertyChanged();
            SaveState();
        }
    }

    private DayOfWeek FirstDayOfWeek => _settings?.FirstDayOfWeek ?? WeekStart.Resolve(WeekStartSetting.System, CultureInfo.CurrentCulture);

    partial void OnModeChanged(ViewMode value)
    {
        SaveState();
        Content = value switch
        {
            ViewMode.Day => _day,
            ViewMode.Week => _week,
            ViewMode.Month => _month,
            ViewMode.Upcoming => _upcoming,
            _ => _tasks,
        };
        OnPropertyChanged(nameof(IsDay));
        OnPropertyChanged(nameof(IsWeek));
        OnPropertyChanged(nameof(IsMonth));
        OnPropertyChanged(nameof(IsUpcoming));
        OnPropertyChanged(nameof(IsTasks));
        OnPropertyChanged(nameof(CanNavigate));
        _ = ReloadAsync();
    }

    partial void OnCurrentDateChanged(DateOnly value) => _ = ReloadAsync();

    partial void OnShowEventsChanged(bool value)
    {
        SaveState();
        _ = ReloadAsync();
    }

    partial void OnShowTasksChanged(bool value)
    {
        SaveState();
        _ = ReloadAsync();
    }

    partial void OnShowCompletedChanged(bool value)
    {
        SaveState();
        _ = ReloadAsync();
    }

    [RelayCommand]
    private void SetMode(ViewMode mode) => Mode = mode;

    [RelayCommand]
    private void GoToday() => CurrentDate = Today();

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(1);

    [RelayCommand]
    private void LoadMore()
    {
        _upcoming.LoadMore();
        _ = ReloadAsync();
    }

    /// <summary>Refresh: syncs every source (when there is an engine) and then re-queries the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_engine is not null)
        {
            await _engine.SyncAllAsync();
        }

        await ReloadAsync();
    }

    [RelayCommand]
    private void OpenSources() => _openSources?.Invoke();

    [RelayCommand]
    private void OpenSettings() => _openSettings?.Invoke();

    /// <summary>Loads collections and the first view. Call once after the window is shown.</summary>
    public async Task InitializeAsync()
    {
        await RestoreStateAsync();
        await LoadCollectionsAsync();
        if (_engine is not null)
        {
            foreach (var status in await _engine.GetStatusesAsync())
            {
                _statuses[status.SourceId] = status;
            }

            UpdateSummary();
        }

        await ReloadAsync();
    }

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        if (_engine is not null)
        {
            _engine.SourceSynced -= OnSourceSynced;
            _engine.StatusChanged -= OnStatusChanged;
        }

        _midnightTimer.Dispose();
        _tasks.Dispose();
        _loading?.Cancel();
        _loading?.Dispose();
    }

    private async Task RestoreStateAsync()
    {
        if (_state is null)
        {
            return;
        }

        await _tasks.RestoreAsync();
        if (await _state.LoadAsync<ShellState>("view.shell") is not { } saved)
        {
            return;
        }

        _restoring = true;
        try
        {
            ShowEvents = saved.ShowEvents;
            ShowTasks = saved.ShowTasks;
            ShowCompleted = saved.ShowCompleted;
            HideWeekends = saved.HideWeekends;
            if (Enum.TryParse<ViewMode>(saved.Mode, out var mode))
            {
                Mode = mode;
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void SaveState()
    {
        if (_state is null || _restoring)
        {
            return;
        }

        _ = _state.SaveAsync("view.shell", new ShellState(Mode.ToString(), ShowEvents, ShowTasks, ShowCompleted, HideWeekends));
    }

    /// <summary>Re-reads collections and the current view after the user changed a collection's visibility or colour.</summary>
    public async Task OnCollectionsChangedAsync()
    {
        await LoadCollectionsAsync();
        await ReloadAsync();
    }

    // Sync events arrive on background threads and often in bursts (one per source), so they are coalesced.
    private void OnSourceSynced(object? sender, SourceSyncedEventArgs e)
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            Interlocked.Exchange(ref _reloadScheduled, 0);
            await LoadCollectionsAsync();
            await ReloadAsync();
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());

    private void OnStatusChanged(object? sender, SourceStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            _statuses[status.SourceId] = status;
            UpdateSummary();
        });

    private void UpdateSummary()
    {
        var all = _statuses.Values.ToList();
        var attention = all.Count(s => s.Kind is SourceStatusKind.NeedsAttention);
        var errors = all.Count(s => s.Kind is SourceStatusKind.Error);
        var offline = all.Count(s => s.Kind is SourceStatusKind.Offline);
        IsSyncing = all.Any(s => s.Kind == SourceStatusKind.Syncing);
        HasProblems = attention + errors + offline > 0;
        SyncSummary = IsSyncing ? "Syncing…"
            : attention > 0 ? $"{attention} source{(attention == 1 ? string.Empty : "s")} need{(attention == 1 ? "s" : string.Empty)} attention"
            : errors > 0 ? $"{errors} source{(errors == 1 ? string.Empty : "s")} failed to sync"
            : offline > 0 ? "Offline: showing saved data"
            : all.Count == 0 ? "No sources"
            : "All sources up to date";
    }

    private void OpenDay(DateOnly day)
    {
        CurrentDate = day;
        Mode = ViewMode.Day;
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).DateTime);

    private void OnTick()
    {
        var today = Today();
        if (today == _today)
        {
            return;
        }

        // Keep following "today" if the user was looking at it.
        var wasToday = CurrentDate == _today;
        _today = today;
        if (wasToday)
        {
            CurrentDate = today;
        }
        else
        {
            _ = ReloadAsync();
        }
    }

    private void Step(int direction)
    {
        CurrentDate = Mode switch
        {
            ViewMode.Day => CurrentDate.AddDays(direction),
            ViewMode.Week => CurrentDate.AddDays(7 * direction),
            ViewMode.Month => CurrentDate.AddMonths(direction),
            _ => CurrentDate,
        };
    }

    private async Task LoadCollectionsAsync()
    {
        var collections = await _sources.GetCollectionsAsync();
        Collections.Clear();
        foreach (var c in collections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Collections.Add(new CollectionToggle(c, _sources, ReloadAsync));
        }
    }

    private async Task ReloadAsync()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var cts = _loading = new CancellationTokenSource();
        var now = _clock.GetUtcNow();
        var culture = CultureInfo.CurrentCulture;
        Title = FormatTitle(culture);

        var context = new ViewContext(CurrentDate, new ViewFilter(ShowEvents, ShowTasks, ShowCompleted), now, _zone, FirstDayOfWeek);
        IsLoading = true;
        try
        {
            await Content.LoadAsync(context, cts.Token);
            LastRefreshed = $"Updated {TimeZoneInfo.ConvertTime(now, _zone).ToString("t", culture)}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private string FormatTitle(CultureInfo culture)
    {
        switch (Mode)
        {
            case ViewMode.Day:
                return CurrentDate.ToString("D", culture);
            case ViewMode.Week:
                var start = WeekLayout.StartOfWeek(CurrentDate, FirstDayOfWeek);
                var end = start.AddDays(6);
                return start.Month == end.Month
                    ? $"{start.Day}–{end.ToString("d MMM yyyy", culture)}"
                    : $"{start.ToString("d MMM", culture)} – {end.ToString("d MMM yyyy", culture)}";
            case ViewMode.Month:
                return CurrentDate.ToString("MMMM yyyy", culture);
            case ViewMode.Upcoming:
                return "Upcoming";
            default:
                return "All tasks";
        }
    }
}
