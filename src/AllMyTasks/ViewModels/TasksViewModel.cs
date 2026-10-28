using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;
using AllMyTasks.Core.Views;
using AllMyTasks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

public sealed class TaskGroupHeader(string text, int count)
{
    public string Text { get; } = text;

    public string CountText { get; } = count.ToString(CultureInfo.CurrentCulture);
}

public sealed class TaskRowItem
{
    public TaskRowItem(TaskRow row, int depth, DateTimeOffset now, TimeZoneInfo zone, Func<ItemKey, bool, bool, string, Task>? toggle = null)
    {
        var task = row.Task;
        Key = task.Key;
        Title = string.IsNullOrWhiteSpace(task.Title) ? "(no title)" : task.Title;
        Depth = depth;
        Indent = new Avalonia.Thickness(depth * 24, 0, 0, 0);
        IsCompleted = task.IsCompleted;
        IsCancelled = task.Status == TaskItemStatus.Cancelled;
        IsOverdue = row.IsOverdue;
        Glyph = task.IsCompleted ? "☑" : task.Status == TaskItemStatus.Cancelled ? "☒" : "☐";
        DueText = task.Due switch
        {
            DueDate d => d.Date.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            DueDateTime at => FormatDateTime(TimeZoneInfo.ConvertTime(at.At, zone)),
            _ => string.Empty,
        };
        CollectionName = row.CollectionName;
        SourceName = row.SourceName;
        Accent = Avalonia.Media.Brush.Parse(ColorUtil.OrDefault(row.Color));
        PriorityText = task.Priority switch { Priority.High => "!!!", Priority.Medium => "!!", Priority.Low => "!", _ => string.Empty };
        LabelsText = string.Join(", ", task.Labels);
        SubtaskText = row.Children.Count > 0 ? $"{row.Children.Count} subtask{(row.Children.Count == 1 ? string.Empty : "s")}" : string.Empty;
        Opacity = task.IsOpen ? 1 : 0.55;
        CanChange = row.CanChange && toggle is not null && task.Status != TaskItemStatus.Cancelled;
        IsRecurring = task.Recurrence is not null;
        ToggleName = task.IsCompleted ? "Mark as not done" : "Mark as done";
        if (row.Pending is { State: PendingState.Failed } failed)
        {
            FailedText = "Not sent: " + (failed.Error ?? "the source refused the change") + ". Open the task to retry or discard.";
        }

        ToggleCommand = new RelayCommand(() =>
        {
            if (toggle is not null)
            {
                _ = toggle(Key, !task.IsCompleted, IsRecurring, Title);
            }
        });
        Spoken = $"{Title}, {(IsOverdue ? "overdue, " : string.Empty)}{(DueText.Length > 0 ? "due " + DueText + ", " : string.Empty)}{CollectionName}";
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("ddd d MMM", CultureInfo.CurrentCulture) + ", " + value.ToString("t", CultureInfo.CurrentCulture);

    public ItemKey Key { get; }

    public string Title { get; }

    public int Depth { get; }

    public Avalonia.Thickness Indent { get; }

    public bool IsCompleted { get; }

    public bool IsCancelled { get; }

    public bool IsOverdue { get; }

    public string Glyph { get; }

    public string DueText { get; }

    public string CollectionName { get; }

    public string SourceName { get; }

    public Avalonia.Media.IBrush Accent { get; }

    public string PriorityText { get; }

    public string LabelsText { get; }

    public string SubtaskText { get; }

    public double Opacity { get; }

    public string Spoken { get; }

    /// <summary>Whether the glyph is a button that completes or reopens the task.</summary>
    public bool CanChange { get; }

    public bool CannotChange => !CanChange;

    public bool IsRecurring { get; }

    public string ToggleName { get; }

    public ICommand ToggleCommand { get; }

    /// <summary>Why the last change to this task did not reach the source; empty when nothing failed.</summary>
    public string FailedText { get; } = string.Empty;

    public bool HasFailed => FailedText.Length > 0;
}

/// <summary>Option shown in a combo box.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>State of the All tasks view: its filters, sort and grouping, and the resulting rows.</summary>
public sealed partial class TasksViewModel(
    ITaskListService service,
    TimeProvider clock,
    ViewStateStore? state = null,
    Func<ItemKey, bool, bool, string, Task>? toggle = null) : ContentViewModel, IDisposable
{
    internal const string StateKey = "view.tasks";
    private bool _restoring;
    private ViewContext? _context;
    private CancellationTokenSource? _debounce;

    public static IReadOnlyList<Choice<TaskStatusFilter>> StatusChoices { get; } =
    [
        new("Open", TaskStatusFilter.Open),
        new("Completed", TaskStatusFilter.Completed),
        new("Cancelled", TaskStatusFilter.Cancelled),
        new("All", TaskStatusFilter.All),
    ];

    public static IReadOnlyList<Choice<DueOption?>> DueChoices { get; } =
    [
        new("Any due date", null),
        new("Overdue", DueOption.Overdue),
        new("Today", DueOption.Today),
        new("Next 7 days", DueOption.Next7Days),
        new("This month", DueOption.ThisMonth),
        new("No due date", DueOption.NoDueDate),
    ];

    public static IReadOnlyList<Choice<TaskSortField>> SortChoices { get; } =
    [
        new("Due date", TaskSortField.Due),
        new("Priority", TaskSortField.Priority),
        new("Title", TaskSortField.Title),
        new("Source", TaskSortField.Source),
        new("Last modified", TaskSortField.LastModified),
    ];

    public static IReadOnlyList<Choice<TaskGrouping>> GroupChoices { get; } =
    [
        new("No grouping", TaskGrouping.None),
        new("Due date", TaskGrouping.DueBucket),
        new("Collection", TaskGrouping.Collection),
        new("Priority", TaskGrouping.Priority),
        new("Label", TaskGrouping.Label),
    ];

    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOpen), nameof(IsStatusCompleted), nameof(IsStatusCancelled), nameof(IsStatusAll), nameof(HasActiveFilters))]
    public partial Choice<TaskStatusFilter> Status { get; set; } = StatusChoices[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueChip), nameof(IsDueActive), nameof(HasActiveFilters))]
    public partial Choice<DueOption?> Due { get; set; } = DueChoices[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortSummary))]
    public partial Choice<TaskSortField> Sort { get; set; } = SortChoices[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupSummary))]
    public partial Choice<TaskGrouping> Grouping { get; set; } = GroupChoices[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortSummary))]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityChip), nameof(IsPriorityActive), nameof(HasActiveFilters))]
    public partial bool High { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityChip), nameof(IsPriorityActive), nameof(HasActiveFilters))]
    public partial bool Medium { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityChip), nameof(IsPriorityActive), nameof(HasActiveFilters))]
    public partial bool Low { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityChip), nameof(IsPriorityActive), nameof(HasActiveFilters))]
    public partial bool NoPriority { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelsChip), nameof(IsLabelsActive), nameof(HasActiveFilters))]
    public partial string LabelsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AllLabels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool IsStatusOpen => Status.Value == TaskStatusFilter.Open;

    public bool IsStatusCompleted => Status.Value == TaskStatusFilter.Completed;

    public bool IsStatusCancelled => Status.Value == TaskStatusFilter.Cancelled;

    public bool IsStatusAll => Status.Value == TaskStatusFilter.All;

    [RelayCommand]
    private void SetStatus(string name) => Status = StatusChoices.First(c => c.Value.ToString() == name);

    public string DueChip => Due.Value is null ? "Due date" : Due.Label;

    public string PriorityChip => PriorityNames() is { Count: > 0 } names ? "Priority: " + string.Join(", ", names) : "Priority";

    public string LabelsChip => LabelsText.Trim().Length > 0 ? "Labels: " + LabelsText.Trim() : "Labels";

    public string SortSummary => $"Sort: {Sort.Label} {(Descending ? "↓" : "↑")}";

    public string GroupSummary => Grouping.Value == TaskGrouping.None ? "No grouping" : "Group: " + Grouping.Label;

    public bool IsDueActive => Due.Value is not null;

    public bool IsPriorityActive => High || Medium || Low || NoPriority;

    public bool IsLabelsActive => LabelsText.Trim().Length > 0;

    /// <summary>Whether anything narrows the list beyond the default (open tasks, everything else open).</summary>
    public bool HasActiveFilters =>
        Status.Value != TaskStatusFilter.Open || IsDueActive || IsPriorityActive || IsLabelsActive || SearchText.Trim().Length > 0;

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        try
        {
            Status = StatusChoices[0];
            Due = DueChoices[0];
            High = Medium = Low = NoPriority = false;
            LabelsText = string.Empty;
            AllLabels = false;
            SearchText = string.Empty;
        }
        finally
        {
            _restoring = false;
        }

        Refresh();
    }

    private List<string> PriorityNames()
    {
        var names = new List<string>();
        if (High)
        {
            names.Add("High");
        }

        if (Medium)
        {
            names.Add("Medium");
        }

        if (Low)
        {
            names.Add("Low");
        }

        if (NoPriority)
        {
            names.Add("None");
        }

        return names;
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
    }

    /// <summary>Restores the filter, sort and grouping from the previous session.</summary>
    public async Task RestoreAsync()
    {
        if (state is null || await state.LoadAsync<TasksState>(StateKey) is not { } saved)
        {
            return;
        }

        _restoring = true;
        try
        {
            Status = StatusChoices.FirstOrDefault(c => c.Value.ToString() == saved.Status) ?? StatusChoices[0];
            Due = DueChoices.FirstOrDefault(c => c.Value?.ToString() == saved.Due) ?? DueChoices[0];
            Sort = SortChoices.FirstOrDefault(c => c.Value.ToString() == saved.Sort) ?? SortChoices[0];
            Grouping = GroupChoices.FirstOrDefault(c => c.Value.ToString() == saved.Grouping) ?? GroupChoices[0];
            Descending = saved.Descending;
            High = saved.High;
            Medium = saved.Medium;
            Low = saved.Low;
            NoPriority = saved.NoPriority;
            LabelsText = saved.Labels;
            AllLabels = saved.AllLabels;
        }
        finally
        {
            _restoring = false;
        }
    }

    public override Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return RunAsync(cancellationToken);
    }

    partial void OnStatusChanged(Choice<TaskStatusFilter> value) => Refresh();

    partial void OnDueChanged(Choice<DueOption?> value) => Refresh();

    partial void OnSortChanged(Choice<TaskSortField> value) => Refresh();

    partial void OnGroupingChanged(Choice<TaskGrouping> value) => Refresh();

    partial void OnDescendingChanged(bool value) => Refresh();

    partial void OnHighChanged(bool value) => Refresh();

    partial void OnMediumChanged(bool value) => Refresh();

    partial void OnLowChanged(bool value) => Refresh();

    partial void OnNoPriorityChanged(bool value) => Refresh();

    partial void OnAllLabelsChanged(bool value) => Refresh();

    // Text and labels are re-run on every keystroke, so they are debounced.
    partial void OnSearchTextChanged(string value) => Refresh(debounce: true);

    partial void OnLabelsTextChanged(string value) => Refresh(debounce: true);

    private void Refresh(bool debounce = false)
    {
        if (_restoring)
        {
            return;
        }

        if (state is not null)
        {
            _ = state.SaveAsync(StateKey, new TasksState(
                Status.Value.ToString(),
                Due.Value?.ToString(),
                Sort.Value.ToString(),
                Grouping.Value.ToString(),
                Descending,
                High,
                Medium,
                Low,
                NoPriority,
                LabelsText,
                AllLabels));
        }

        if (_context is null)
        {
            return;
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = RefreshAsync(debounce, cts.Token);
    }

    private async Task RefreshAsync(bool debounce, CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellationToken);
            }

            await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke or change superseded this one.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var context = _context!;
        var priorities = new HashSet<Priority>();
        if (High)
        {
            priorities.Add(Priority.High);
        }

        if (Medium)
        {
            priorities.Add(Priority.Medium);
        }

        if (Low)
        {
            priorities.Add(Priority.Low);
        }

        if (NoPriority)
        {
            priorities.Add(Priority.None);
        }

        var labels = LabelsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = Due.Value is { } option ? new DueFilter(new HashSet<DueOption> { option }) : DueFilter.Any;
        var filter = new TaskFilter(Status.Value, due, new HashSet<CollectionRef>(), priorities, labels, AllLabels, SearchText);
        var query = new TaskListQuery(filter, new TaskSort(Sort.Value, Descending), Grouping.Value, context.Zone);

        var groups = await service.GetAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Items.Clear();
        var count = 0;
        foreach (var group in groups)
        {
            if (Grouping.Value != TaskGrouping.None)
            {
                Items.Add(new TaskGroupHeader(GroupHeading(group), group.Rows.Count));
            }

            foreach (var row in group.Rows)
            {
                count += Add(row, 0, context);
            }
        }

        Summary = count == 1 ? "1 task" : $"{count} tasks";
    }

    private int Add(TaskRow row, int depth, ViewContext context)
    {
        Items.Add(new TaskRowItem(row, depth, context.Now, context.Zone, toggle));
        return 1 + row.Children.Sum(c => Add(c, depth + 1, context));
    }

    private static string GroupHeading(TaskGroup group) => group.Bucket switch
    {
        DueBucket.Overdue => "Overdue",
        DueBucket.Earlier => "Earlier",
        DueBucket.Today => "Today",
        DueBucket.ThisWeek => "This week",
        DueBucket.Later => "Later",
        DueBucket.NoDate => "No due date",
        _ => group.Priority is { } p ? $"{p} priority" : group.Label.Length > 0 ? group.Label : "No label",
    };
}
