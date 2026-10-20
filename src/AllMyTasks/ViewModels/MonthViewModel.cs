using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

public sealed class MonthCellViewModel
{
    public const int MaxItems = 3;

    public MonthCellViewModel(DayCell cell, int laneCount, DateTimeOffset now, TimeZoneInfo zone, Action<DateOnly> openDay)
    {
        Date = cell.Date;
        DayNumber = cell.Date.Day.ToString(CultureInfo.CurrentCulture);
        IsCurrentMonth = cell.IsCurrentMonth;
        IsToday = cell.IsToday;
        var room = Math.Max(1, MaxItems - laneCount);
        Items = [.. cell.Items.Take(cell.Items.Count > room ? room - 1 : room).Select(s => new EntryItem(s, now, zone))];
        Hidden = Math.Max(0, cell.Items.Count - Items.Count);
        MoreText = Hidden > 0 ? $"+{Hidden} more" : string.Empty;
        ItemsMargin = new Avalonia.Thickness(0, laneCount * 20, 0, 0);
        OpenDayCommand = new RelayCommand(() => openDay(Date));
    }

    public DateOnly Date { get; }

    public string DayNumber { get; }

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    public IReadOnlyList<EntryItem> Items { get; }

    public int Hidden { get; }

    public string MoreText { get; }

    public bool HasMore => Hidden > 0;

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

        Rows.Clear();
        foreach (var row in grid.Rows)
        {
            var bars = row.SpanBars
                .Select(b => new SpanBarItem(b.Entry, b.StartColumn, b.ColumnSpan, b.Lane, b.ContinuedFromBefore, b.ContinuesAfter, context.Now, context.Zone))
                .ToList();
            Rows.Add(new MonthRowViewModel(
                [.. row.Days.Select(c => new MonthCellViewModel(c, row.LaneCount, context.Now, context.Zone, openDay))],
                bars,
                row.LaneCount));
        }

        WeekdayHeaders = [.. Enumerable.Range(0, 7)
            .Select(i => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)context.FirstDayOfWeek + i) % 7)))];
        OnPropertyChanged(nameof(WeekdayHeaders));
    }
}
