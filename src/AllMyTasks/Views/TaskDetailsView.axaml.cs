using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

public sealed partial class TaskDetailsView : UserControl
{
    public TaskDetailsView() => InitializeComponent();

    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        // Core only exposes http and https links here; anything else never reaches the launcher.
        if (DataContext is TaskDetailsViewModel { Link: { } link } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(link);
        }
    }
}
