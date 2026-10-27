using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lighthouse.Core;

namespace Lighthouse;

/// <summary>Saves a window's content as a PNG, as it would look on screen, without showing the window.</summary>
internal static class ImageExport
{
    public static void Save(Window window, double width, string path) => Write(Render(window, width, solidBackground: true), path);

    /// <summary>
    /// The window's content laid out at a width, at the screen's scale unless <paramref name="pixelsPerInch"/> says
    /// (192 is twice the size, for sharp pictures). Without a solid background, see-through parts stay see-through.
    /// </summary>
    public static RenderTargetBitmap Render(Window window, double width, bool solidBackground, double? pixelsPerInch = null)
    {
        var root = (Border)window.Content;
        var background = root.Background;
        if (solidBackground) root.Background = Theme.Brush("Bg");
        Layout(window, width);
        var dpi = VisualTreeHelper.GetDpi(window);
        double ppiX = pixelsPerInch ?? dpi.PixelsPerInchX, ppiY = pixelsPerInch ?? dpi.PixelsPerInchY;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * ppiX / 96), (int)Math.Ceiling(root.ActualHeight * ppiY / 96),
            ppiX, ppiY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        root.Background = background;
        return bitmap;
    }

    public static void Layout(Window window, double width)
    {
        var root = (Border)window.Content;
        root.Measure(new Size(width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
        root.UpdateLayout();
    }

    /// <summary>A striped stand-in desktop, light or dark, for checking see-through windows in a picture.</summary>
    public static RenderTargetBitmap Backdrop(int pixelWidth, int pixelHeight, double dpiX, double dpiY, bool dark)
    {
        double w = pixelWidth * 96 / dpiX, h = pixelHeight * 96 / dpiY;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(dark
                ? new LinearGradientBrush(Color.FromRgb(16, 20, 24), Color.FromRgb(52, 40, 60), 35)
                : new LinearGradientBrush(Color.FromRgb(46, 97, 113), Color.FromRgb(226, 109, 92), 35), null, new Rect(0, 0, w, h));
            var stripe = new SolidColorBrush(dark ? Color.FromArgb(150, 90, 100, 110) : Color.FromArgb(150, 250, 252, 250));
            for (double x = -h; x < w; x += 28)
            {
                var band = new StreamGeometry();
                using (var g = band.Open())
                {
                    g.BeginFigure(new Point(x, h), true, true);
                    g.LineTo(new Point(x + 10, h), true, false);
                    g.LineTo(new Point(x + 10 + h, 0), true, false);
                    g.LineTo(new Point(x + h, 0), true, false);
                }
                dc.DrawGeometry(stripe, null, band);
            }
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    public static RenderTargetBitmap Over(BitmapSource backdrop, BitmapSource content)
    {
        var rect = new Rect(0, 0, content.Width, content.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(backdrop, rect);
            dc.DrawImage(content, rect);
        }
        var bitmap = new RenderTargetBitmap(content.PixelWidth, content.PixelHeight, content.DpiX, content.DpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    public static void Write(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // A fresh file: the running copy is elevated, and File.Create would write through a planted link.
        if (File.Exists(path) || Paths.IsLink(path)) File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }
}
