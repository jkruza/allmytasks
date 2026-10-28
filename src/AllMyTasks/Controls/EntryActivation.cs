using AllMyTasks.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AllMyTasks.Controls;

/// <summary>
/// Makes an entry (a row, chip or bar bound to an <see cref="EntryItem"/>, or a row of the All tasks list bound to a <see cref="TaskRowItem"/>) open the details pane on click and on
/// Enter or Space. Lives here so the entry view models do not need a reference to the shell.
/// </summary>
public static class EntryActivation
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(EntryActivation));

    static EntryActivation() => EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.Tapped -= OnTapped;
        control.KeyDown -= OnKeyDown;
        control.Focusable = false;
        if (e.NewValue is true)
        {
            control.Focusable = true;
            control.Cursor = new Cursor(StandardCursorType.Hand);
            control.Tapped += OnTapped;
            control.KeyDown += OnKeyDown;
        }
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        // A button inside the row (the completion checkbox) handles its own click.
        if (sender is Visual row && e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is { } button && row.IsVisualAncestorOf(button))
        {
            return;
        }

        Open(sender, e);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None || !ReferenceEquals(e.Source, sender))
        {
            return;
        }

        // On a task that can be changed, Space ticks it and Enter opens it.
        if (e.Key == Key.Space && sender is Control { DataContext: TaskRowItem { CanChange: true } row })
        {
            row.ToggleCommand.Execute(null);
            e.Handled = true;
            return;
        }

        Open(sender, e);
    }

    private static void Open(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: { } context } control
            || TopLevel.GetTopLevel(control)?.DataContext is not CalendarShellViewModel shell)
        {
            return;
        }

        switch (context)
        {
            case EntryItem item:
                _ = shell.ShowDetailsAsync(item.Entry);
                e.Handled = true;
                break;
            case TaskRowItem row:
                _ = shell.ShowTaskDetailsAsync(row.Key);
                e.Handled = true;
                break;
        }
    }
}
