using AllMyTasks.Core.Reminders;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

/// <summary>
/// A small topmost window for one reminder, used when the OS cannot show notifications. It never takes focus when
/// it appears, so it does not interrupt typing.
/// </summary>
public sealed partial class ReminderWindow : Window
{
    private readonly Action<NotificationAction>? _onAction;

    public ReminderWindow()
        : this(Reminder.Missed(0, DateTimeOffset.UnixEpoch), null)
    {
    }

    public ReminderWindow(Reminder reminder, Action<NotificationAction>? onAction)
    {
        InitializeComponent();
        _onAction = onAction;
        Heading.Text = reminder.Title;
        Body.Text = reminder.Detail;
        Body.IsVisible = !string.IsNullOrEmpty(reminder.Detail);
        SnoozeButton.IsVisible = reminder.CanSnooze;
        DismissButton.Content = reminder.CanSnooze ? "Dismiss" : "Close";
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => Finish(NotificationAction.Open);

    private void OnSnooze(object? sender, RoutedEventArgs e) => Finish(NotificationAction.Snooze);

    private void OnDismiss(object? sender, RoutedEventArgs e) => Finish(NotificationAction.Dismiss);

    private void Finish(NotificationAction action)
    {
        _onAction?.Invoke(action);
        Close();
    }
}
