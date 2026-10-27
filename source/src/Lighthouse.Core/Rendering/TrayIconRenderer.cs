using System.Drawing;
using System.Drawing.Drawing2D;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Rendering;

/// <summary>Lighthouse's palette, with darker stand-ins where the light taskbar needs contrast.</summary>
public static class Palette
{
    public static readonly Color PaynesGray = ColorTranslator.FromHtml("#2e6171");
    public static readonly Color PaynesGrayDark = ColorTranslator.FromHtml("#234c59");
    public static readonly Color Aquahaze = ColorTranslator.FromHtml("#edf3f5");
    public static readonly Color Honeydew = ColorTranslator.FromHtml("#d9e5d6");
    public static readonly Color ResedaGreen = ColorTranslator.FromHtml("#5b7553");
    public static readonly Color Khaki = ColorTranslator.FromHtml("#f1e38f");
    public static readonly Color KhakiDeep = ColorTranslator.FromHtml("#b8962e");
    public static readonly Color Bittersweet = ColorTranslator.FromHtml("#e26d5c");
    public static readonly Color BittersweetDeep = ColorTranslator.FromHtml("#c4483a");
    public static readonly Color Ink = ColorTranslator.FromHtml("#1d2c31");
}

public enum Heat { Cool, Warm, Hot }

/// <summary>
/// The taskbar icon: two bars, CPU left and GPU right, filled by load and coloured by temperature.
/// The returned HICON belongs to the caller, who must DestroyIcon it once the tray has the next one.
/// </summary>
public static class TrayIconRenderer
{
    public static Heat CpuHeat(double? temp) => temp switch { >= 85 => Heat.Hot, >= 72 => Heat.Warm, _ => Heat.Cool };

    public static Heat GpuHeat(double? edge, double? hotspot) =>
        edge >= 85 || hotspot >= 100 ? Heat.Hot : edge >= 72 || hotspot >= 90 ? Heat.Warm : Heat.Cool;

    public static Color ColorFor(Heat heat, bool lightTaskbar) => heat switch
    {
        Heat.Hot => lightTaskbar ? Palette.BittersweetDeep : Palette.Bittersweet,
        Heat.Warm => lightTaskbar ? Palette.KhakiDeep : Palette.Khaki,
        _ => lightTaskbar ? Palette.PaynesGray : Palette.Aquahaze,
    };

    /// <summary>Pixel rows filled for a load, so the icon is only redrawn when something visible changes.</summary>
    public static int FillRows(double load, int barHeight) =>
        load <= 0.5 ? 0 : Math.Clamp((int)Math.Round(load / 100 * barHeight), 1, barHeight);

    /// <summary>
    /// Layout at a given icon size. 16 px: a 4 px lamp at the top centre, then two 6 px bars (9 px tall) with a
    /// 2 px gap and a 1 px margin all round. Everything scales with the icon.
    /// </summary>
    public readonly record struct Layout(int Margin, int Lamp, int Gap, int BarWidth, int BarTop, int BarHeight)
    {
        public static Layout For(int size)
        {
            int margin = Math.Max(1, size / 16);
            int lamp = Math.Max(4, size / 4);
            int gap = Math.Max(2, size / 8);
            int barTop = margin + lamp + Math.Max(1, size / 16);
            return new Layout(margin, lamp, gap, (size - 2 * margin - gap) / 2, barTop, size - margin - barTop);
        }
    }

    /// <summary>The lamp shows the hotter chip's colour.</summary>
    public static Heat LampHeat(Heat cpu, Heat gpu) => (Heat)Math.Max((int)cpu, (int)gpu);

    public static Bitmap Render(int size, double cpuLoad, Heat cpuHeat, double gpuLoad, Heat gpuHeat, bool lightTaskbar,
        bool lampLit = true)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);

        var l = Layout.For(size);
        DrawBar(g, l.Margin, l.BarTop, l.BarWidth, l.BarHeight, cpuLoad, ColorFor(cpuHeat, lightTaskbar));
        DrawBar(g, l.Margin + l.BarWidth + l.Gap, l.BarTop, l.BarWidth, l.BarHeight, gpuLoad, ColorFor(gpuHeat, lightTaskbar));
        DrawLamp(g, size, l, ColorFor(LampHeat(cpuHeat, gpuHeat), lightTaskbar), lampLit);
        return bmp;
    }

    private static void DrawLamp(Graphics g, int size, Layout l, Color color, bool lit)
    {
        var shade = lit ? color : Color.FromArgb(60, color);
        int x = (size - l.Lamp) / 2, y = l.Margin;
        if (l.Lamp < 5)
        {
            // Too small for a smooth circle: a square with half-strength corners reads as a dot.
            using var solid = new SolidBrush(shade);
            using var soft = new SolidBrush(Color.FromArgb(shade.A / 2, shade));
            g.FillRectangle(solid, x, y, l.Lamp, l.Lamp);
            g.CompositingMode = CompositingMode.SourceCopy;
            foreach (var (cx, cy) in new[] { (x, y), (x + l.Lamp - 1, y), (x, y + l.Lamp - 1), (x + l.Lamp - 1, y + l.Lamp - 1) })
                g.FillRectangle(soft, cx, cy, 1, 1);
            g.CompositingMode = CompositingMode.SourceOver;
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(shade))
            g.FillEllipse(brush, x, y, l.Lamp, l.Lamp);
        g.SmoothingMode = SmoothingMode.None;
    }

    private static void DrawBar(Graphics g, int x, int y, int width, int height, double load, Color color)
    {
        using (var track = new SolidBrush(Color.FromArgb(70, color)))
            g.FillRectangle(track, x, y, width, height);
        int rows = FillRows(load, height);
        if (rows == 0) return;
        using var fill = new SolidBrush(color);
        g.FillRectangle(fill, x, y + height - rows, width, rows);
    }

    public static IntPtr RenderHicon(int size, double cpuLoad, Heat cpuHeat, double gpuLoad, Heat gpuHeat, bool lightTaskbar,
        bool lampLit = true)
    {
        using var bmp = Render(size, cpuLoad, cpuHeat, gpuLoad, gpuHeat, lightTaskbar, lampLit);
        return bmp.GetHicon();
    }

    public static void Destroy(IntPtr hicon)
    {
        if (hicon != IntPtr.Zero) NativeMethods.DestroyIcon(hicon);
    }
}
