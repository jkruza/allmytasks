using AllMyTasks.ViewModels;
using Avalonia.Controls;

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
}
