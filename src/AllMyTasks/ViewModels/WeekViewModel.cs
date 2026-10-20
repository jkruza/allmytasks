using System.Collections.ObjectModel;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

public sealed class WeekDayViewModel(DayColumn column, DateTimeOffset now, TimeZoneInfo zone)
{
    public DateOnly Date { get; } = column.Date;

    public string Header { get; } = column.Date.ToString("ddd d", System.Globalization.CultureInfo.CurrentCulture);

    public bool IsToday { get; } = column.IsToday;

    public int TotalMinutes { get; } = column.TotalMinutes;

    public ObservableCollection<TimedEntryItem> Entries { get; } = [.. column.Timed.Select(p => new TimedEntryItem(p, now, zone))];
}

public sealed partial class WeekViewModel(IAgendaService agenda) : ContentViewModel
{
    public const double PixelsPerMinute = 0.8;

    public ObservableCollection<WeekDayViewModel> Days { get; } = [];

    public ObservableCollection<SpanBarItem> AllDayBars { get; } = [];

    public ObservableCollection<string> HourLabels { get; } = [];

    [ObservableProperty]
    public partial int ColumnCount { get; set; } = 7;

    [ObservableProperty]
    public partial int VisibleStartMinute { get; set; }

    [ObservableProperty]
    public partial double TimelineHeight { get; set; }

    [ObservableProperty]
    public partial double AllDayHeight { get; set; }

    [ObservableProperty]
    public partial bool HideWeekends { get; set; }

    public bool HasAllDay => AllDayBars.Count > 0;

    private ViewContext? _last;

    partial void OnHideWeekendsChanged(bool value)
    {
        if (_last is { } ctx)
        {
            _ = LoadAsync(ctx, CancellationToken.None);
        }
    }

    public override async Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        _last = context;
        var start = WeekLayout.StartOfWeek(context.Date, context.FirstDayOfWeek);
        var query = new ViewQuery(start, start.AddDays(7), context.Zone, context.Filter);
        var entries = await agenda.GetEntriesAsync(query, cancellationToken);
        var layout = WeekLayout.Build(start, entries, context.Now, context.Zone, HideWeekends);

        Days.Clear();
        foreach (var d in layout.Days)
        {
            Days.Add(new WeekDayViewModel(d, context.Now, context.Zone));
        }

        AllDayBars.Clear();
        foreach (var b in layout.AllDaySpans)
        {
            AllDayBars.Add(new SpanBarItem(b.Entry, b.StartColumn, b.ColumnSpan, b.Lane, b.ContinuedFromBefore, b.ContinuesAfter, context.Now, context.Zone));
        }

        HourLabels.Clear();
        for (var h = layout.VisibleStartHour; h < layout.VisibleEndHour; h++)
        {
            HourLabels.Add(new TimeOnly(h, 0).ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        }

        ColumnCount = layout.Days.Count;
        VisibleStartMinute = layout.VisibleStartHour * 60;
        TimelineHeight = (layout.VisibleEndHour - layout.VisibleStartHour) * 60 * PixelsPerMinute;
        AllDayHeight = layout.AllDayLaneCount * 22;
        OnPropertyChanged(nameof(HasAllDay));
    }
}
