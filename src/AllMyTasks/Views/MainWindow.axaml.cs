using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AllMyTasks.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
