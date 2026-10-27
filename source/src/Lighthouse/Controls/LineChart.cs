using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Lighthouse.Core.History;

namespace Lighthouse.Controls;

public sealed record ChartSeries(string Name, Brush Brush, IReadOnlyList<double?> Values);

/// <summary>
/// A small time chart: gridlines, a legend, round time ticks, and series decimated to the width. A null value breaks
/// the line (nothing was recorded then).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(IReadOnlyList<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(nameof(Times),
        typeof(IReadOnlyList<DateTime>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum),
        typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit),
        typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From),
        typeof(DateTime?), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To),
        typeof(DateTime?), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FitLowProperty = DependencyProperty.Register(nameof(FitLow),
        typeof(bool), typeof(LineChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText),
        typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("No history yet: it fills in a minute at a time.",
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The time span shown; unset, the data's own first and last times.</summary>
    public DateTime? From { get => (DateTime?)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTime? To { get => (DateTime?)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    /// <summary>Start the axis near the lowest value instead of 0 (temperatures, which never get near 0).</summary>
    public bool FitLow { get => (bool)GetValue(FitLowProperty); set => SetValue(FitLowProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<DateTime>? Times { get => (IReadOnlyList<DateTime>?)GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    /// <summary>0 = fit the data.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var series = Series;
        var times = Times;
        var muted = Theme.Brush("Muted");
        var line = Theme.Brush("Line");
        const double left = 40, top = 22, bottom = 18;
        double plotW = w - left - 6, plotH = h - top - bottom;
        if (plotW <= 10 || plotH <= 10) return;

        var known = (series ?? []).SelectMany(s => s.Values).Where(v => v is not null).Select(v => v!.Value).ToList();
        double max = Maximum > 0 ? Maximum : Math.Max(1, known.DefaultIfEmpty(1).Max() * 1.1);
        double min = FitLow && known.Count > 0 ? Math.Max(0, Math.Floor(known.Min() * 0.9 / 10) * 10) : 0;
        if (FitLow) max = Math.Ceiling(max / 10) * 10;
        if (max <= min) max = min + 1;

        var gridPen = new Pen(line, 1);
        for (int i = 0; i <= 2; i++)
        {
            double y = top + plotH * i / 2;
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + plotW, y));
            if (known.Count > 0) // no numbers on an empty chart
                Text(dc, $"{min + (max - min) * (2 - i) / 2:0}{Unit}", muted, new Point(left - 4, y - 7), alignRight: true);
        }

        if (series is null || times is null || times.Count == 0 || known.Count == 0)
        {
            Text(dc, EmptyText, muted, new Point(left + 8, top + plotH / 2 - 8));
            return;
        }

        DateTime from = From ?? times[0], to = To ?? times[^1];
        if (to <= from) to = from.AddMinutes(1);
        double t0 = from.Ticks, span = to.Ticks - t0;
        // Round times along the bottom ("15:00", "Tue"); a label that would run off either end is left out.
        var ticks = ChartData.Ticks(from, to, 7);
        var step = ticks.Count > 1 ? ticks[1] - ticks[0] : to - from;
        foreach (var tick in ticks)
        {
            double x = left + (tick.Ticks - t0) / span * plotW;
            dc.DrawLine(gridPen, new Point(x, top + plotH), new Point(x, top + plotH + 3));
            var label = Format(ChartData.TickLabel(tick, step), muted);
            if (x - label.Width / 2 >= left - 2 && x + label.Width / 2 <= left + plotW + 4)
                dc.DrawText(label, new Point(x - label.Width / 2, top + plotH + 3));
        }

        double legendX = left;
        foreach (var s in series)
        {
            dc.DrawRectangle(s.Brush, null, new Rect(legendX, 4, 10, 10));
            var ft = Text(dc, s.Name, muted, new Point(legendX + 14, 1));
            legendX += 14 + ft.Width + 14;
            DrawSeries(dc, s, times, left, top, plotW, plotH, min, max, t0, span);
        }
    }

    private static void DrawSeries(DrawingContext dc, ChartSeries s, IReadOnlyList<DateTime> times, double left, double top,
        double plotW, double plotH, double min, double max, double t0, double span)
    {
        int n = Math.Min(times.Count, s.Values.Count);
        if (n < 1) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            int buckets = (int)Math.Max(2, plotW);
            int perBucket = Math.Max(1, n / buckets);
            for (int i = 0; i < n; i += perBucket)
            {
                // Keep the peak of each bucket so a decimated week still shows its spikes.
                double? peak = null;
                for (int j = i; j < Math.Min(n, i + perBucket); j++)
                    if (s.Values[j] is { } v) peak = peak is null ? v : Math.Max(peak.Value, v);
                if (peak is null)
                {
                    open = false;
                    continue;
                }
                var p = new Point(left + Math.Clamp((times[i].Ticks - t0) / span, 0, 1) * plotW,
                    top + plotH - Math.Clamp((peak.Value - min) / (max - min), 0, 1) * plotH);
                if (!open) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
                open = true;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(s.Brush, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private FormattedText Text(DrawingContext dc, string text, Brush brush, Point at, bool alignRight = false)
    {
        var ft = Format(text, brush);
        dc.DrawText(ft, alignRight ? new Point(at.X - ft.Width, at.Y) : at);
        return ft;
    }

    private FormattedText Format(string text, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
