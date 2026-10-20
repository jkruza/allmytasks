using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

public sealed partial class SourceEditorWindow : Window
{
    public SourceEditorWindow() => InitializeComponent();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SourceEditorViewModel editor && await editor.SaveAsync())
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
