using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AllMyTasks.Views;

public sealed partial class SourcesWindow : Window
{
    public SourcesWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is SourcesViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnOpenLogFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SourcesViewModel { LogFolder: { } folder } && Launcher is { } launcher)
        {
            Directory.CreateDirectory(folder);
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
    }
}
