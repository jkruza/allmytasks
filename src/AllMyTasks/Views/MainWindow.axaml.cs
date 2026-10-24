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

    // Keyboard shortcuts: T today, D/W/M/U/A switch view, ←/→ previous/next, R refresh.
    // Ignored while typing in a text box.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source is TextBox || DataContext is not CalendarShellViewModel shell || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when shell.IsDetailsOpen:
                shell.CloseDetailsCommand.Execute(null);
                break;
            case Key.T:
                shell.GoTodayCommand.Execute(null);
                break;
            case Key.D:
                shell.Mode = ViewMode.Day;
                break;
            case Key.W:
                shell.Mode = ViewMode.Week;
                break;
            case Key.M:
                shell.Mode = ViewMode.Month;
                break;
            case Key.U:
                shell.Mode = ViewMode.Upcoming;
                break;
            case Key.A:
                shell.Mode = ViewMode.Tasks;
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
