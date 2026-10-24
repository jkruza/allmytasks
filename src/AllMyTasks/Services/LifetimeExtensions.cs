using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AllMyTasks.Services;

internal static class LifetimeExtensions
{
    /// <summary>
    /// The window a new dialog should be owned by: the one the user is working in, else the main window.
    /// Non-modal windows such as Sources can be in front of the main window. A dialog owned by the main
    /// window then opens behind them, and some Linux window managers answer with an "is ready"
    /// notification instead of raising it.
    /// </summary>
    public static Window? GetDialogOwner(this IClassicDesktopStyleApplicationLifetime lifetime) =>
        lifetime.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible) ?? lifetime.MainWindow;
}
