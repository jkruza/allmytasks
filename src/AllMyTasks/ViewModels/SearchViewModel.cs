using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Search;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

/// <summary>An event in the search results.</summary>
public sealed class SearchEventItem
{
    public SearchEventItem(EventHit hit, TimeZoneInfo zone, Func<EventHit, Task> showInCalendar)
    {
        Hit = hit;
        var start = TimeZoneInfo.ConvertTime(hit.Start, zone);
        Title = string.IsNullOrWhiteSpace(hit.Event.Title) ? "(no title)" : hit.Event.Title;
        WhenText = hit.IsAllDay
            ? start.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)
            : start.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture) + ", " + start.ToString("t", CultureInfo.CurrentCulture);
        Detail = string.Join(" · ", new[] { hit.IsRecurring ? "Repeats" : null, hit.Event.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
        CollectionName = hit.CollectionName;
        Accent = Avalonia.Media.Brush.Parse(ColorUtil.OrDefault(hit.Color));
        Opacity = hit.IsPast ? 0.7 : 1;
        Spoken = $"Event: {Title}, {WhenText}, {CollectionName}";
        ShowInCalendarCommand = new RelayCommand(() => _ = showInCalendar(hit));
    }

    public EventHit Hit { get; }

    public string Title { get; }

    public string WhenText { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public string CollectionName { get; }

    public Avalonia.Media.IBrush Accent { get; }

    public double Opacity { get; }

    public string Spoken { get; }

    public ICommand ShowInCalendarCommand { get; }
}

/// <summary>A task in the search results.</summary>
public sealed class SearchTaskItem
{
    public SearchTaskItem(TaskHit hit, TimeZoneInfo zone)
    {
        var task = hit.Task;
        Key = task.Key;
        Title = string.IsNullOrWhiteSpace(task.Title) ? "(no title)" : task.Title;
        Glyph = task.IsCompleted ? "☑" : task.Status == TaskItemStatus.Cancelled ? "☒" : "☐";
        DueText = task.Due switch
        {
            DueDate d => d.Date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture),
            DueDateTime at => TimeZoneInfo.ConvertTime(at.At, zone).ToString("ddd d MMM yyyy, t", CultureInfo.CurrentCulture),
            _ => string.Empty,
        };
        IsOverdue = hit.IsOverdue;
        CollectionName = hit.CollectionName;
        Accent = Avalonia.Media.Brush.Parse(ColorUtil.OrDefault(hit.Color));
        Opacity = task.IsOpen ? 1 : 0.55;
        Spoken = $"Task: {Title}, {(IsOverdue ? "overdue, " : string.Empty)}{(DueText.Length > 0 ? "due " + DueText + ", " : string.Empty)}{CollectionName}";
    }

    public ItemKey Key { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string DueText { get; }

    public bool IsOverdue { get; }

    public string CollectionName { get; }

    public Avalonia.Media.IBrush Accent { get; }

    public double Opacity { get; }

    public string Spoken { get; }
}

/// <summary>
/// Global search over events and tasks (see docs/design/search-and-saved-filters.md). It is shown in place of the
/// current view and reads only the local cache.
/// </summary>
public sealed partial class SearchViewModel : ContentViewModel, IDisposable
{
    private readonly ISearchService _service;
    private readonly TimeProvider _clock;
    private readonly Func<EventHit, Task> _showInCalendar;
    private ViewContext? _context;
    private CancellationTokenSource? _debounce;

    public SearchViewModel(ISearchService service, TimeProvider clock, Func<EventHit, Task> showInCalendar, Action close)
    {
        _service = service;
        _clock = clock;
        _showInCalendar = showInCalendar;
        CloseCommand = new RelayCommand(close);
    }

    /// <summary>Raised when the query box should take the keyboard focus, for example on a second Ctrl+F.</summary>
    public event EventHandler? FocusRequested;

    public ObservableCollection<object> Items { get; } = [];

    public ICommand CloseCommand { get; }

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    /// <summary>"3 events and 2 tasks", "No matches", or a hint to type.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "Type to search events and tasks.";

    /// <summary>"4 more in hidden calendars and lists", or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    public partial string HiddenText { get; set; } = string.Empty;

    public bool HasHidden => HiddenText.Length > 0;

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public override Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return RunAsync(cancellationToken);
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
    }

    // Typing re-runs the search, but only once the user pauses; a newer keystroke cancels the older search.
    partial void OnQueryTextChanged(string value)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = DebouncedAsync(cts.Token);
    }

    private async Task DebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), _clock, cancellationToken);
            await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this one.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var text = QueryText.Trim();
        if (_context is null || text.Length == 0)
        {
            Items.Clear();
            HiddenText = string.Empty;
            Summary = "Type to search events and tasks.";
            return;
        }

        var results = await _service.SearchAsync(new SearchQuery(text), _context.Zone, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Items.Clear();
        if (results.Events.Count > 0)
        {
            Items.Add(new TaskGroupHeader("Events", results.Events.Count));
            foreach (var hit in results.Events)
            {
                Items.Add(new SearchEventItem(hit, _context.Zone, _showInCalendar));
            }
        }

        if (results.Tasks.Count > 0)
        {
            Items.Add(new TaskGroupHeader("Tasks", results.Tasks.Count));
            foreach (var hit in results.Tasks)
            {
                Items.Add(new SearchTaskItem(hit, _context.Zone));
            }
        }

        Summary = results.Count == 0
            ? "No matches"
            : $"{Plural(results.Events.Count, "event")} and {Plural(results.Tasks.Count, "task")}";
        HiddenText = results.HiddenByFilter > 0
            ? $"{results.HiddenByFilter} more in hidden calendars and lists"
            : string.Empty;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
