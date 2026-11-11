using System.Collections.ObjectModel;
using System.Globalization;
using AllMyTasks.Core.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AllMyTasks.ViewModels;

public sealed class DayGroupHeader(string text)
{
    public string Text { get; } = text;
}

public sealed partial class UpcomingViewModel(IAgendaService agenda) : ContentViewModel
{
    private int _days = 14;

    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "Nothing coming up";

    public void LoadMore() => _days += 14;

    public override async Task LoadAsync(ViewContext context, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).DateTime);
        var query = new ViewQuery(today, today.AddDays(_days), context.Zone, context.Filter, IncludeOverdue: true);
        var entries = await agenda.GetEntriesAsync(query, cancellationToken);
        var list = UpcomingList.Build(entries, context.Now, context.Zone, _days);
        var conflicts = Conflicts = context.Conflicts is { } options ? ConflictFinder.Find(entries, options) : ConflictMap.Empty;

        Items.Clear();
        foreach (var group in list.Groups)
        {
            Items.Add(new DayGroupHeader(Heading(group)));
            foreach (var s in group.Segments)
            {
                Items.Add(new EntryItem(s, context.Now, context.Zone, conflicts));
            }
        }

        IsEmpty = list.IsEmpty;
        EmptyText = context.FirstSyncRunning ? "Waiting for the first sync…" : "Nothing coming up";
    }

    private static string Heading(DayGroup group) => group.Kind switch
    {
        DayGroupKind.Overdue => "Overdue",
        DayGroupKind.Today => "Today",
        DayGroupKind.Tomorrow => "Tomorrow",
        _ => group.Date!.Value.ToString("dddd, d MMMM", CultureInfo.CurrentCulture),
    };
}
