using AllMyTasks.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace AllMyTasks.Controls;

/// <summary>
/// Places all-day bars across a row of equal columns from precomputed column and lane numbers. Children's
/// data contexts implement <see cref="ISpanPlacement"/>.
/// </summary>
public sealed class SpanBarPanel : Panel
{
    public static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<SpanBarPanel, int>(nameof(ColumnCount), 7);

    public static readonly StyledProperty<double> LaneHeightProperty =
        AvaloniaProperty.Register<SpanBarPanel, double>(nameof(LaneHeight), 20);

    static SpanBarPanel() =>
        AffectsMeasure<SpanBarPanel>(ColumnCountProperty, LaneHeightProperty);

    public int ColumnCount
    {
        get => GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public double LaneHeight
    {
        get => GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width;
        var lanes = 0;
        foreach (var child in Children)
        {
            var p = child.DataContext as ISpanPlacement;
            child.Measure(new Size(width * (p?.ColumnSpan ?? 1) / Math.Max(1, ColumnCount), LaneHeight));
            lanes = Math.Max(lanes, (p?.Lane ?? 0) + 1);
        }

        return new Size(width, lanes * LaneHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = finalSize.Width / Math.Max(1, ColumnCount);
        foreach (var child in Children)
        {
            var p = child.DataContext as ISpanPlacement;
            var x = (p?.StartColumn ?? 0) * columnWidth;
            var width = (p?.ColumnSpan ?? 1) * columnWidth;
            child.Arrange(new Rect(x + 1, (p?.Lane ?? 0) * LaneHeight, Math.Max(1, width - 2), LaneHeight - 2));
        }

        return finalSize;
    }
}
