using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AllMyTasks.Views;

/// <summary>Shows the diagnostics report so the user can read it before copying it. See docs/design/source-diagnostics.md.</summary>
public sealed partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
        : this(string.Empty)
    {
    }

    public DiagnosticsWindow(string report)
    {
        InitializeComponent();
        ReportBox.Text = report;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, ReportBox.Text ?? string.Empty);
            Copied.IsVisible = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
