using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AllMyTasks.Views;

public sealed partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusQuery();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SearchViewModel search)
        {
            search.FocusRequested += (_, _) => FocusQuery();
        }
    }

    // Esc closes the details pane first, then the search.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && TopLevel.GetTopLevel(this)?.DataContext is CalendarShellViewModel shell)
        {
            if (shell.IsDetailsOpen)
            {
                shell.CloseDetailsCommand.Execute(null);
            }
            else
            {
                shell.CloseSearch();
            }

            e.Handled = true;
        }
    }

    private void FocusQuery() => Dispatcher.UIThread.Post(() =>
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    });
}
