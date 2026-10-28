using AllMyTasks.Core.Reminders;
using AllMyTasks.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AllMyTasks.Services;

/// <summary>
/// Shows reminders in a small topmost window of the app itself. It is the notifier on systems without a usable
/// OS notification path (Windows and macOS until the installers exist, see docs/design/reminders.md) and the
/// fallback when the Linux notification program is missing.
/// </summary>
internal sealed class InAppNotifier : INotifier
{
    private static readonly int EdgeMargin = 16;
    private readonly List<ReminderWindow> _open = [];

    public bool SupportsActions => true;

    public event EventHandler<NotificationActivatedEventArgs>? Activated;

    public Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return Dispatcher.UIThread.InvokeAsync(() => Show(reminder), DispatcherPriority.Normal, cancellationToken).GetTask();
    }

    private void Show(Reminder reminder)
    {
        var window = new ReminderWindow(reminder, action => Activated?.Invoke(this, new NotificationActivatedEventArgs(reminder, action)));
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Restack();
        };
        _open.Add(window);
        window.Opened += (_, _) => Restack();
        window.Show();
    }

    /// <summary>Stacks the open windows up from the bottom-right corner of the primary screen.</summary>
    private void Restack()
    {
        var screen = _open.Count == 0 ? null : _open[0].Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var y = area.Bottom - EdgeMargin;
        foreach (var window in _open.AsEnumerable().Reverse())
        {
            var scaling = window.Screens.ScreenFromWindow(window)?.Scaling ?? screen.Scaling;
            var width = (int)(window.Bounds.Width * scaling);
            var height = (int)(window.Bounds.Height * scaling);
            y -= height;
            window.Position = new PixelPoint(area.Right - width - EdgeMargin, y);
            y -= EdgeMargin / 2;
        }
    }
}
