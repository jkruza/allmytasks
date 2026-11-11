using AllMyTasks.Core.About;
using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnOpenWebsite(object? sender, RoutedEventArgs e) => await OpenAsync(ProjectLinks.Website);

    private async void OnOpenRepository(object? sender, RoutedEventArgs e) => await OpenAsync(ProjectLinks.Repository);

    private async void OnOpenIssues(object? sender, RoutedEventArgs e) => await OpenAsync(ProjectLinks.Issues);

    private async void OnCopyDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.About.ToText());
        }
    }

    private async Task OpenAsync(string url)
    {
        if (Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(url));
        }
    }
}
