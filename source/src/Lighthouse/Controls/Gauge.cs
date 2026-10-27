using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lighthouse.Controls;

/// <summary>A 270° ring gauge whose arc eases to each new value.</summary>
public sealed class Gauge : FrameworkElement
{
    /// <summary>Off while the flyout is saved as an image, so the picture shows the values, not mid-sweep.</summary>
    public static bool Animate { get; set; } = true;

    /// <summary>Gauges made so far. It should stop rising once the windows exist: a rising count means WPF is
    /// rebuilding them, which makes every gauge sweep up from zero.</summary>
    public static int Created { get; private set; }

    public Gauge() => Created++;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double),
        typeof(Gauge), new PropertyMetadata(0.0, (d, e) => ((Gauge)d).MoveTo((double)e.NewValue)));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double),
        typeof(Gauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(Gauge), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush),
        typeof(Gauge), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(Gauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush),
        typeof(Gauge), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(nameof(RingThickness),
        typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }

    private void MoveTo(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (!Animate || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, value);
            return;
        }
        var ease = new DoubleAnimation(value, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        // WPF animates at the display's refresh rate (180 Hz on the main monitor) unless told otherwise; four
        // gauges easing every second at that rate was most of the mini monitor's CPU (measured 2026-10-22).
        Timeline.SetDesiredFrameRate(ease, 30);
        BeginAnimation(ShownProperty, ease);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        double thickness = RingThickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - thickness / 2 - 1;
        dc.DrawGeometry(null, Pen(Track, thickness), Arc(center, radius, 1));
        double shown = (double)GetValue(ShownProperty);
        if (shown > 0.003) dc.DrawGeometry(null, Pen(Stroke, thickness), Arc(center, radius, shown));

        var ft = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.24, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static Pen Pen(Brush brush, double thickness) =>
        new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    /// <summary>An arc starting bottom-left (135°) and running clockwise through the given share of 270°.</summary>
    private static StreamGeometry Arc(Point c, double r, double fraction)
    {
        const double start = 135;
        double sweep = 270 * Math.Clamp(fraction, 0, 1);
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(start), false, false);
            ctx.ArcTo(At(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
