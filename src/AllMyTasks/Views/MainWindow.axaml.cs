using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AllMyTasks.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CalendarShellViewModel shell)
        {
            // Move focus into the pane when it opens so keyboard and screen reader users land in it.
            shell.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CalendarShellViewModel.SelectedDetails) && shell.IsDetailsOpen)
                {
                    Dispatcher.UIThread.Post(() => CloseDetailsButton.Focus());
                }
            };
        }
    }

    // Keyboard shortcuts: Ctrl+F (Cmd+F) search, T today, D/W/M/U/A switch view, ←/→ previous/next, R refresh.
    // All but search are ignored while typing in a text box.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.F && (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta) && DataContext is CalendarShellViewModel searching)
        {
            searching.OpenSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Handled || e.Source is TextBox || DataContext is not CalendarShellViewModel shell || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when shell.IsDetailsOpen:
                shell.CloseDetailsCommand.Execute(null);
                break;
            case Key.Escape when shell.IsSearching:
                shell.CloseSearch();
                break;
            case Key.T:
                shell.GoTodayCommand.Execute(null);
                break;
            case Key.D:
                shell.SetModeCommand.Execute(ViewMode.Day);
                break;
            case Key.W:
                shell.SetModeCommand.Execute(ViewMode.Week);
                break;
            case Key.M:
                shell.SetModeCommand.Execute(ViewMode.Month);
                break;
            case Key.U:
                shell.SetModeCommand.Execute(ViewMode.Upcoming);
                break;
            case Key.I:
                shell.SetModeCommand.Execute(ViewMode.Inbox);
                break;
            case Key.A:
                shell.SetModeCommand.Execute(ViewMode.Tasks);
                break;
            case Key.Left:
                shell.PreviousCommand.Execute(null);
                break;
            case Key.Right:
                shell.NextCommand.Execute(null);
                break;
            case Key.R:
                _ = shell.RefreshAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
