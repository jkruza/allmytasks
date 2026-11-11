using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Settings;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

public sealed class DayGroupHeader(string text)
{
    public string Text { get; } = text;
}

public sealed partial class UpcomingViewModel(
    IAgendaService agenda,
    Func<AppSettings>? settings = null,
    Action<DateOnly>? openDay = null,
    Action? openOverdue = null) : ContentViewModel
{
    private int _days = 14;

    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>The seven-day strip; null when it is switched off or the view is empty.</summary>
    [ObservableProperty]
    public partial WorkloadStripViewModel? Workload { get; set; }

    public void LoadMore() => _days += 14;

    public override async Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).DateTime);
        var query = new ViewQuery(today, today.AddDays(_days), context.Zone, context.Filter, IncludeOverdue: true);
        var entries = await agenda.GetEntriesAsync(query, cancellationToken);
        var list = UpcomingList.Build(entries, context.Now, context.Zone, _days);

        Items.Clear();
        foreach (var group in list.Groups)
        {
            Items.Add(new DayGroupHeader(Heading(group)));
            foreach (var s in group.Segments)
            {
                Items.Add(new EntryItem(s, context.Now, context.Zone));
            }
        }

        IsEmpty = list.IsEmpty;

        var current = settings?.Invoke();
        Workload = current is { ShowWorkload: true } && !list.IsEmpty
            ? new WorkloadStripViewModel(WorkloadSummary.Build(entries, today, context.Zone, current.ToWorkloadOptions()), today, openDay ?? (_ => { }), openOverdue ?? (() => { }))
            : null;
    }

    private static string Heading(DayGroup group) => group.Kind switch
    {
        DayGroupKind.Overdue => "Overdue",
        DayGroupKind.Today => "Today",
        DayGroupKind.Tomorrow => "Tomorrow",
        _ => group.Date!.Value.ToString("dddd, d MMMM", CultureInfo.CurrentCulture),
    };
}
