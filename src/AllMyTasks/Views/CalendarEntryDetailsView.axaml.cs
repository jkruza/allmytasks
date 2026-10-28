using AllMyTasks.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

public sealed partial class CalendarEntryDetailsView : UserControl
{
    public CalendarEntryDetailsView() => InitializeComponent();

    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        // Core only exposes http and https links here; anything else never reaches the launcher.
        if (DataContext is CalendarEntryDetailsViewModel { Link: { } link } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(link);
        }
    }

    private async void OnJoinMeeting(object? sender, RoutedEventArgs e)
    {
        // Only on a click, and only the https link Core validated; it is never opened by itself.
        if (DataContext is CalendarEntryDetailsViewModel { MeetingUrl: { } link } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(link);
        }
    }

    private async void OnCopyMeetingLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarEntryDetailsViewModel { MeetingUrl: { } link } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(link.AbsoluteUri);
        }
    }
}
