using System.Windows;
using System.Windows.Media;

namespace Lighthouse.Controls;

/// <summary>A minimal line with a soft fill, for the last minute on each tile.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values),
        typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum),
        typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke),
        typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;
        double max = Maximum > 0 ? Maximum : Math.Max(1, values.Max());
        double step = w / (values.Count - 1);
        Point At(int i) => new(i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 1) - 0.5);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (int i = 1; i < values.Count; i++) ctx.LineTo(At(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < values.Count; i++) ctx.LineTo(At(i), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        area.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.14;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round }, line);
    }
}
