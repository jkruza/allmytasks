using AllMyTasks.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AllMyTasks.Controls;

/// <summary>
/// Places children on a day's time axis from numbers precomputed in Core. Children's data contexts
/// implement <see cref="ITimelinePlacement"/>; the panel does no layout decisions of its own.
/// Draws an hour grid behind them.
/// </summary>
public sealed class TimelinePanel : Panel
{
    public static readonly StyledProperty<double> PixelsPerMinuteProperty =
        AvaloniaProperty.Register<TimelinePanel, double>(nameof(PixelsPerMinute), 0.8);

    public static readonly StyledProperty<int> OriginMinuteProperty =
        AvaloniaProperty.Register<TimelinePanel, int>(nameof(OriginMinute));

    public static readonly StyledProperty<double> TimelineHeightProperty =
        AvaloniaProperty.Register<TimelinePanel, double>(nameof(TimelineHeight));

    private readonly HourLines _lines = new();

    static TimelinePanel() =>
        AffectsMeasure<TimelinePanel>(PixelsPerMinuteProperty, OriginMinuteProperty, TimelineHeightProperty);

    public TimelinePanel() => Children.Add(_lines);

    public double PixelsPerMinute
    {
        get => GetValue(PixelsPerMinuteProperty);
        set => SetValue(PixelsPerMinuteProperty, value);
    }

    /// <summary>The minute of day at the top edge.</summary>
    public int OriginMinute
    {
        get => GetValue(OriginMinuteProperty);
        set => SetValue(OriginMinuteProperty, value);
    }

    public double TimelineHeight
    {
        get => GetValue(TimelineHeightProperty);
        set => SetValue(TimelineHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width;
        _lines.PixelsPerMinute = PixelsPerMinute;
        _lines.Measure(new Size(width, TimelineHeight));
        foreach (var child in Children.Where(c => c != _lines))
        {
            var (_, height, columns) = Place(child, width);
            child.Measure(new Size(width / columns, Math.Max(1, height)));
        }

        return new Size(width, TimelineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _lines.Arrange(new Rect(finalSize));
        foreach (var child in Children.Where(c => c != _lines))
        {
            var (rect, _, _) = Place(child, finalSize.Width);
            child.Arrange(rect);
        }

        return finalSize;
    }

    private (Rect Rect, double Height, int Columns) Place(Control child, double width)
    {
        if (child.DataContext is not ITimelinePlacement p)
        {
            return (new Rect(0, 0, width, 20), 20, 1);
        }

        var top = (p.StartMinute - OriginMinute) * PixelsPerMinute;
        var height = Math.Max(14, (p.EndMinute - p.StartMinute) * PixelsPerMinute);
        var columns = Math.Max(1, p.ColumnCount);
        var columnWidth = width / columns;
        return (new Rect((p.Column * columnWidth) + 1, top, Math.Max(1, columnWidth - 2), height - 1), height, columns);
    }

    /// <summary>The hour grid behind the entries. Transparent to hit tests.</summary>
    private sealed class HourLines : Control
    {
        public HourLines() => IsHitTestVisible = false;

        public double PixelsPerMinute { get; set; } = 0.8;

        public override void Render(DrawingContext context)
        {
            var pen = new Pen(new SolidColorBrush(Colors.Gray, 0.25), 1);
            var step = 60 * PixelsPerMinute;
            for (var y = 0.0; y <= Bounds.Height; y += step)
            {
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }
    }
}
