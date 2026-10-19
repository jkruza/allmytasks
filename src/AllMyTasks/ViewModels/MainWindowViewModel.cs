using System.Collections.ObjectModel;
using AllMyTasks.Core.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AllMyTasks.ViewModels;

public sealed partial class MainWindowViewModel(TaskAggregator aggregator) : ViewModelBase
{
    public ObservableCollection<TaskItem> Tasks { get; } = [];

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
                Tasks.Add(task);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
