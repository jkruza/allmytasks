using System.Collections.ObjectModel;
using AllMyTasks.Core.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

public sealed partial class MainWindowViewModel(TaskAggregator aggregator, TimeZoneInfo displayZone) : ViewModelBase
{
    public ObservableCollection<TaskRow> Tasks { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            var tasks = await aggregator.GetAllAsync(cancellationToken);
            Tasks.Clear();
            foreach (var task in tasks)
            {
                Tasks.Add(TaskRow.From(task, displayZone));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
