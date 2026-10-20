using System.Collections.ObjectModel;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

public sealed partial class DayViewModel(IAgendaService agenda) : ContentViewModel
{
    public ObservableCollection<EntryItem> AllDay { get; } = [];

    public ObservableCollection<object> Timeline { get; } = [];

    public ObservableCollection<EntryItem> Overdue { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    public bool HasAllDay => AllDay.Count > 0;

    public bool HasOverdue => Overdue.Count > 0;

    public override async Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).DateTime);
        var query = new ViewQuery(context.Date, context.Date.AddDays(1), context.Zone, context.Filter, IncludeOverdue: context.Date == today);
        var entries = await agenda.GetEntriesAsync(query, cancellationToken);
        var day = DayAgenda.Build(context.Date, entries, context.Now, context.Zone);

        AllDay.Clear();
        foreach (var s in day.AllDay)
        {
            AllDay.Add(new EntryItem(s, context.Now, context.Zone));
        }

        Timeline.Clear();
        for (var i = 0; i < day.Timed.Count; i++)
        {
            if (day.NowIndex == i)
            {
                Timeline.Add(NowMarker.Instance);
            }

            Timeline.Add(new EntryItem(day.Timed[i], context.Now, context.Zone));
        }

        if (day.NowIndex == day.Timed.Count && day.Timed.Count > 0)
        {
            Timeline.Add(NowMarker.Instance);
        }

        Overdue.Clear();
        foreach (var s in day.Overdue)
        {
            Overdue.Add(new EntryItem(s, context.Now, context.Zone));
        }

        IsEmpty = day.IsEmpty;
        EmptyText = "Nothing planned";
        OnPropertyChanged(nameof(HasAllDay));
        OnPropertyChanged(nameof(HasOverdue));
    }
}

/// <summary>Marks the current time in the day timeline.</summary>
public sealed class NowMarker
{
    public static NowMarker Instance { get; } = new();
}
