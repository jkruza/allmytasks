using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

public sealed class MonthCellViewModel : ObservableObject
{
    /// <summary>Entries shown before the real cell height is known, so the first frame is not empty.</summary>
    public const int InitialItems = 3;

    private readonly IReadOnlyList<EntryItem> _all;
    private IReadOnlyList<EntryItem> _items;
    private int _hidden;

    public MonthCellViewModel(DayCell cell, int laneCount, DateTimeOffset now, TimeZoneInfo zone, Action<DateOnly> openDay, ConflictMap? conflicts = null)
    {
        Date = cell.Date;
        DayNumber = cell.Date.Day.ToString(CultureInfo.CurrentCulture);
        IsCurrentMonth = cell.IsCurrentMonth;
        IsToday = cell.IsToday;
        LaneCount = laneCount;
        _all = [.. cell.Items.Select(s => new EntryItem(s, now, zone, conflicts))];
        _items = [];
        ShowCount(Math.Min(_all.Count, Math.Max(1, InitialItems - laneCount)));
        ItemsMargin = new Avalonia.Thickness(0, laneCount * 20, 0, 0);
        OpenDayCommand = new RelayCommand(() => openDay(Date));
    }

    public DateOnly Date { get; }

    public string DayNumber { get; }

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public bool IsRecessed => IsWeekend && !IsToday;

    public int LaneCount { get; }

    public int TotalItems => _all.Count;

    public IReadOnlyList<EntryItem> Items => _items;

    public int Hidden => _hidden;

    public string MoreText => _hidden > 0 ? $"+{_hidden} more" : string.Empty;

    public bool HasMore => _hidden > 0;

    /// <summary>Re-fits the cell to the height left for entries (below the day number and the span-bar lanes).</summary>
    public void Fit(double availableHeight, double itemHeight, double moreHeight) =>
        ShowCount(MonthCellFit.VisibleCount(_all.Count, availableHeight, itemHeight, moreHeight));

    private void ShowCount(int count)
    {
        if (count == _items.Count && _hidden == _all.Count - count)
        {
            return;
        }

        _items = [.. _all.Take(count)];
        _hidden = _all.Count - _items.Count;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Hidden));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(HasMore));
    }

    public Avalonia.Thickness ItemsMargin { get; }

    public double Opacity => IsCurrentMonth ? 1 : 0.5;

    public IRelayCommand OpenDayCommand { get; }
}

public sealed class MonthRowViewModel(IReadOnlyList<MonthCellViewModel> days, IReadOnlyList<SpanBarItem> bars, int laneCount)
{
    public IReadOnlyList<MonthCellViewModel> Days { get; } = days;

    public IReadOnlyList<SpanBarItem> Bars { get; } = bars;

    public int LaneCount { get; } = laneCount;
}

public sealed partial class MonthViewModel(IAgendaService agenda, Action<DateOnly> openDay) : ContentViewModel
{
    public ObservableCollection<MonthRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> WeekdayHeaders { get; private set; } = [];

    public override async Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        var gridStart = MonthGrid.GridStart(context.Date, context.FirstDayOfWeek);
        var query = new ViewQuery(gridStart, gridStart.AddDays(42), context.Zone, context.Filter);
        var entries = await agenda.GetEntriesAsync(query, cancellationToken);
        var grid = MonthGrid.Build(context.Date, entries, context.Now, context.Zone, context.FirstDayOfWeek);
        var conflicts = Conflicts = context.Conflicts is { } options ? ConflictFinder.Find(entries, options) : ConflictMap.Empty;

        Rows.Clear();
        foreach (var row in grid.Rows)
        {
            var bars = row.SpanBars
                .Select(b => new SpanBarItem(b.Entry, b.StartColumn, b.ColumnSpan, b.Lane, b.ContinuedFromBefore, b.ContinuesAfter, context.Now, context.Zone, conflicts))
                .ToList();
            Rows.Add(new MonthRowViewModel(
                [.. row.Days.Select(c => new MonthCellViewModel(c, row.LaneCount, context.Now, context.Zone, openDay, conflicts))],
                bars,
                row.LaneCount));
        }

        WeekdayHeaders = [.. Enumerable.Range(0, 7)
            .Select(i => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)context.FirstDayOfWeek + i) % 7)))];
        OnPropertyChanged(nameof(WeekdayHeaders));
    }
}
