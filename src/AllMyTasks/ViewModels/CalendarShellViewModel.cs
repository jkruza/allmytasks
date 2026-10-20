using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Views;
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
    private CancellationTokenSource? _loading;
    private DateOnly _today;

    public CalendarShellViewModel(
        IAgendaService agenda,
        ITaskListService taskList,
        ISourceRepository sources,
        TimeProvider clock,
        TimeZoneInfo zone)
    {
        _sources = sources;
        _clock = clock;
        _zone = zone;
        _today = Today();
        CurrentDate = _today;
        _day = new DayViewModel(agenda);
        _week = new WeekViewModel(agenda);
        _month = new MonthViewModel(agenda, OpenDay);
        _upcoming = new UpcomingViewModel(agenda);
        _tasks = new TasksViewModel(taskList, clock);
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
        }
    }

    private static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    partial void OnModeChanged(ViewMode value)
    {
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

    partial void OnShowEventsChanged(bool value) => _ = ReloadAsync();

    partial void OnShowTasksChanged(bool value) => _ = ReloadAsync();

    partial void OnShowCompletedChanged(bool value) => _ = ReloadAsync();

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

    [RelayCommand]
    public Task RefreshAsync() => ReloadAsync();

    /// <summary>Loads collections and the first view. Call once after the window is shown.</summary>
    public async Task InitializeAsync()
    {
        await LoadCollectionsAsync();
        await ReloadAsync();
    }

    public void Dispose()
    {
        _midnightTimer.Dispose();
        _tasks.Dispose();
        _loading?.Cancel();
        _loading?.Dispose();
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
