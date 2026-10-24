using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
