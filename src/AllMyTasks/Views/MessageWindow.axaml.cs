using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

/// <summary>A small modal message with one or two buttons. Closes with true for the primary button.</summary>
public sealed partial class MessageWindow : Window
{
    public MessageWindow()
        : this(string.Empty, string.Empty, "OK", null)
    {
    }

    public MessageWindow(string heading, string body, string primary, string? secondary)
    {
        InitializeComponent();
        Heading.Text = heading;
        Body.Text = body;
        Primary.Content = primary;
        if (secondary is not null)
        {
            Secondary.Content = secondary;
            Secondary.IsVisible = true;
        }
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => Close(true);

    private void OnSecondary(object? sender, RoutedEventArgs e) => Close(false);
}
