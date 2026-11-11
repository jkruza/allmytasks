using AllMyTasks.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AllMyTasks.Controls;

/// <summary>
/// Attached to a month-grid cell: whenever the cell is laid out at a new size, works out how much height is left for
/// entries and asks the cell view model to show as many as fit. The chip height is measured from a drawn chip when
/// there is one, so it follows font size and theme.
/// </summary>
public static class CellFit
{
    private const double _fallbackItemHeight = 22;
    private const double _fallbackMoreHeight = 18;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(CellFit));

    static CellFit() => EnabledProperty.Changed.AddClassHandler<Border>(OnEnabledChanged);

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(Border cell, AvaloniaPropertyChangedEventArgs e)
    {
        cell.SizeChanged -= OnSizeChanged;
        cell.DataContextChanged -= OnDataContextChanged;
        if (e.NewValue is true)
        {
            cell.SizeChanged += OnSizeChanged;
            cell.DataContextChanged += OnDataContextChanged;
        }
    }

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Fit((Border)sender!);

    private static void OnDataContextChanged(object? sender, EventArgs e) => Fit((Border)sender!);

    private static void Fit(Border cell)
    {
        if (cell.DataContext is not MonthCellViewModel vm || cell.Bounds.Height <= 0)
        {
            return;
        }

        var chrome = cell.Padding.Top + cell.Padding.Bottom + cell.BorderThickness.Top + cell.BorderThickness.Bottom;
        var header = 0.0;
        var itemHeight = _fallbackItemHeight;
        var moreHeight = _fallbackMoreHeight;
        foreach (var child in cell.GetVisualDescendants().OfType<Control>())
        {
            if (child is Button b && b.Content is Border)
            {
                header = b.Bounds.Height;
            }
            else if (child is Border { Classes: var c } chip && c.Contains("chip-box") && chip.Bounds.Height > 0 && chip.Parent is not null)
            {
                itemHeight = chip.Bounds.Height + 2; // chip view margin is 1 above and below
            }
            else if (child is Button { Classes: var cc } more && cc.Contains("tool") && more.IsVisible && more.Bounds.Height > 0)
            {
                moreHeight = more.Bounds.Height;
            }
        }

        var available = cell.Bounds.Height - chrome - header - (vm.LaneCount * 20);
        vm.Fit(available, itemHeight, moreHeight);
    }
}
