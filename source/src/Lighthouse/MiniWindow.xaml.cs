using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lighthouse.Controls;
using Lighthouse.Core;
using Lighthouse.Core.Rendering;
using D = System.Drawing;
using Forms = System.Windows.Forms;

namespace Lighthouse;

/// <summary>
/// The small always-on-top monitor: a strip of the lighthouse scene over four gauges, sharing the flyout's
/// live data. Only its background panel is see-through (Medium by default), at the chosen level under the mouse too.
/// Each gauge's text is white or black for whatever is behind that gauge: once a second it captures the desktop
/// behind itself (it is left out of captures) and picks by contrast. Drag to move (the spot is remembered),
/// double-click for the full panel, right-click for the see-through level and hide. It never takes focus.
/// It gets out of the way of fullscreen video (<see cref="MiniFullscreen"/> keeps the rules).
/// </summary>
public partial class MiniWindow : Window
{
    /// <summary>A fully clear pixel lets clicks fall through to the window behind, so "Clear" keeps a trace.</summary>
    private const double ClearButGrabbable = 0.012;

    private readonly MonitorEngine _engine;
    private readonly List<MiniTile> _tiles;
    private readonly bool _excluded;
    private SolidColorBrush _fill = new(Colors.White);
    private bool _sampleFailureLogged;
    private readonly MiniPlacement _placement = new();
    private bool _dragging, _sliding;
    private readonly MiniFullscreen _fullscreen = new();
    private readonly FullscreenWatch _watch = new();
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MiniWindow(FlyoutViewModel vm, MonitorEngine engine, Action openFlyout, Action hide)
    {
        _engine = engine;
        InitializeComponent();
        DataContext = vm;
        _tiles = vm.Tiles.Select(t => new MiniTile(t)).ToList();
        TileList.ItemsSource = _tiles;
        _excluded = NativeUi.ExcludeFromCapture(this);
        if (!_excluded) Log.Write("mini monitor: Windows refused capture exclusion; text color follows the desktop around its edges");
        PaintPanel();
        IsVisibleChanged += (_, _) => PaintPanel(); // picks up a light/dark theme change
        OpenItem.Click += (_, _) => openFlyout();
        HideItem.Click += (_, _) => hide();
        foreach (var item in SeeThroughItems)
            item.Click += (sender, _) => SetSeeThrough(double.Parse((string)((MenuItem)sender).Tag, CultureInfo.InvariantCulture));
        foreach (var item in LayoutItems)
            item.Click += (sender, _) => SetLayout((string)((MenuItem)sender).Tag);
        foreach (var item in FavoriteItems)
            item.Click += (sender, _) => SetFavorite((string)((MenuItem)sender).Tag);
        foreach (var item in FullscreenItems)
            item.Click += (sender, _) =>
            {
                _engine.Settings.MiniFullscreen = (string)((MenuItem)sender).Tag;
                _engine.SaveSettings();
            };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        AvoidTextItem.Click += (_, _) =>
        {
            _engine.Settings.MiniAvoidText = !_engine.Settings.MiniAvoidText;
            _engine.SaveSettings();
        };
        ContextMenuOpening += (_, _) =>
        {
            MarkSeeThrough();
            foreach (var item in LayoutItems) item.IsChecked = (string)item.Tag == _engine.Settings.MiniLayout;
            foreach (var item in FavoriteItems) item.IsChecked = (string)item.Tag == _engine.Settings.MiniFavorite;
            foreach (var item in FullscreenItems) item.IsChecked = (string)item.Tag == _engine.Settings.MiniFullscreen;
            AvoidTextItem.IsChecked = _engine.Settings.MiniAvoidText;
            AvoidTextItem.IsEnabled = _excluded; // it reads what is behind it, which needs it left out of captures
        };
        ApplyLayout();
        SourceInitialized += (_, _) => SetClickThrough(_engine.Settings.MiniClickThrough);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                openFlyout();
                return;
            }
            _dragging = true;
            try
            {
                DragMove();
            }
            finally
            {
                _dragging = false;
            }
            SnapToEdges();
            _fullscreen.Dragged(); // dropped somewhere while away for fullscreen: it stays there
            _placement.Dragged(DateTime.Now); // for now: it goes back to its favorite spot once that is clear
            SampleBackdrop();
        };
    }

    private IEnumerable<MenuItem> SeeThroughItems => [SolidItem, LightItem, MediumItem, StrongItem];
    private IEnumerable<MenuItem> LayoutItems => [FullLayout, StripLayout, SceneLayout, NumbersLayout];
    private IEnumerable<MenuItem> FavoriteItems => FavoriteMenu.Items.OfType<MenuItem>();
    private IEnumerable<MenuItem> FullscreenItems => FullscreenMenu.Items.OfType<MenuItem>();

    /// <summary>Called once a second with fresh numbers; the text colours and its place are re-checked at the same time.</summary>
    public void UpdateScene(SceneState scene)
    {
        Scene.Update(scene);
        SampleBackdrop();
        CheckPlacement();
    }

    // --- layouts -----------------------------------------------------------------------------------

    private void SetLayout(string layout)
    {
        _engine.Settings.MiniLayout = layout;
        _engine.SaveSettings();
        var dpi = VisualTreeHelper.GetDpi(this);
        var before = WindowPixels(dpi);
        var favorite = Favorite(before.Width, before.Height, dpi);
        bool atFavorite = Math.Abs(favorite.X - before.X) < 4 && Math.Abs(favorite.Y - before.Y) < 4;
        ApplyLayout();
        UpdateLayout();
        if (atFavorite) // a new size moves the favorite spot's corner: keep it there
        {
            var after = WindowPixels(dpi);
            var (x, y) = Favorite(after.Width, after.Height, dpi);
            (Left, Top) = (x / dpi.DpiScaleX, y / dpi.DpiScaleY);
        }
        SampleBackdrop();
    }

    /// <summary>Lighthouse over gauges, side by side (a strip), the lighthouse alone, or the gauges alone.</summary>
    public void ApplyLayout()
    {
        string layout = _engine.Settings.MiniLayout;
        Scene.Visibility = layout == "numbers" ? Visibility.Collapsed : Visibility.Visible;
        TileList.Visibility = layout == "lighthouse" ? Visibility.Collapsed : Visibility.Visible;
        switch (layout)
        {
            case "strip":
                DockPanel.SetDock(Scene, Dock.Left);
                (Scene.Width, Scene.Height, TileList.Margin, LayoutRoot.Width) = (104, 62, new Thickness(8, 0, 0, 0), 104 + 8 + 248);
                break;
            case "lighthouse":
                DockPanel.SetDock(Scene, Dock.Top);
                (Scene.Width, Scene.Height, LayoutRoot.Width) = (double.NaN, 100, 220);
                break;
            case "numbers":
                (TileList.Margin, LayoutRoot.Width) = (new Thickness(0), 268);
                break;
            default:
                DockPanel.SetDock(Scene, Dock.Top);
                (Scene.Width, Scene.Height, TileList.Margin, LayoutRoot.Width) = (double.NaN, 70, new Thickness(0, 8, 0, 0), 268);
                break;
        }
    }

    /// <summary>The width the mini lays out at, for pictures.</summary>
    public double LayoutWidth => LayoutRoot.Width + Panel.Padding.Left + Panel.Padding.Right;

    // --- click-through --------------------------------------------------------------------------------

    /// <summary>Clicks go through to what is behind (switched from the tray menu, since it can't be right-clicked then).</summary>
    public void SetClickThrough(bool on)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
        long style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style = on ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)style);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    // --- moving off text -------------------------------------------------------------------------------

    /// <summary>Snaps to a work-area edge it was dropped near, as far in as the favorite spots sit.</summary>
    private void SnapToEdges()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var me = WindowPixels(dpi);
        var area = Forms.Screen.FromRectangle(me).WorkingArea;
        double l = area.Left / dpi.DpiScaleX, t = area.Top / dpi.DpiScaleY, r = area.Right / dpi.DpiScaleX, b = area.Bottom / dpi.DpiScaleY;
        const double snap = 24, gap = MiniPlacement.Margin;
        if (Math.Abs(Left - l) < snap) Left = l + gap;
        if (Math.Abs(r - (Left + ActualWidth)) < snap) Left = r - ActualWidth - gap;
        if (Math.Abs(Top - t) < snap) Top = t + gap;
        if (Math.Abs(b - (Top + ActualHeight)) < snap) Top = b - ActualHeight - gap;
    }

    /// <summary>
    /// Once a second: if text sits under the mini (or the typing cursor comes near), slide to the calmest spot on its
    /// screen, and back home once home is clear (<see cref="MiniPlacement"/> keeps the rules). Never while the mouse
    /// is over it or it is being dragged.
    /// </summary>
    private void CheckPlacement()
    {
        if (!_engine.Settings.MiniAvoidText || !_excluded || !IsVisible || IsMouseOver || _dragging || _sliding || _fullscreen.IsAway) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var me = WindowPixels(dpi);
            double here = CaretNear(me) ? 1 : TextUnder(me);
            var (hx, hy) = Favorite(me.Width, me.Height, dpi);
            bool atHome = Math.Abs(hx - me.X) < 4 && Math.Abs(hy - me.Y) < 4;
            var home = new Spot("home", hx, hy, atHome ? here : TextUnder(me with { X = hx, Y = hy }));
            var area = Forms.Screen.FromRectangle(me).WorkingArea;
            (int, int)[] avoid = [(me.X, me.Y), (hx, hy)];
            var target = _placement.Check(DateTime.Now, here, atHome, home, () =>
                MiniPlacement.Candidates(area.Left, area.Top, area.Right, area.Bottom, me.Width, me.Height, avoid)
                    .Select(c => new Spot(c.Name, c.X, c.Y, TextUnder(me with { X = c.X, Y = c.Y })))
                    .ToList());
            if (target is null) return;
            Log.Write(target.Name == "home" ? "mini moved back to its favorite spot: it is clear" : $"mini moved to the {target.Name}: text behind it");
            SlideTo(target.X / dpi.DpiScaleX, target.Y / dpi.DpiScaleY);
        }
        catch (Exception e)
        {
            if (!_sampleFailureLogged) Log.Write($"mini monitor could not check what is behind it: {e.Message}");
            _sampleFailureLogged = true;
        }
    }

    private D.Rectangle WindowPixels(DpiScale dpi)
    {
        var origin = PointToScreen(new Point(0, 0));
        return new D.Rectangle((int)origin.X, (int)origin.Y, (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>How much of an area of the screen is text (the mini itself is left out of the capture).</summary>
    private static double TextUnder(D.Rectangle area)
    {
        using var bitmap = new D.Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bitmap))
            g.CopyFromScreen(area.Location, D.Point.Empty, bitmap.Size);
        var data = bitmap.LockBits(new D.Rectangle(D.Point.Empty, bitmap.Size), D.Imaging.ImageLockMode.ReadOnly, D.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return TextScore.Of(bytes, data.Width, data.Height, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The foreground app's typing cursor within 24 px of the mini.</summary>
    private static bool CaretNear(D.Rectangle me)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(0, ref info) || info.hwndCaret == IntPtr.Zero) return false;
        var corner = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        if (!ClientToScreen(info.hwndCaret, ref corner)) return false;
        var caret = new D.Rectangle(corner.X, corner.Y, Math.Max(1, info.rcCaret.Right - info.rcCaret.Left), Math.Max(1, info.rcCaret.Bottom - info.rcCaret.Top));
        return D.Rectangle.Inflate(me, 24, 24).IntersectsWith(caret);
    }

    /// <summary>Slides to a spot over 200 ms (30 fps), then sets it for good so dragging still works.</summary>
    private void SlideTo(double left, double top)
    {
        _sliding = true;
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var x = new DoubleAnimation(Left, left, duration) { EasingFunction = ease };
        var y = new DoubleAnimation(Top, top, duration) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(x, 30);
        Timeline.SetDesiredFrameRate(y, 30);
        x.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            (Left, Top) = (left, top);
            _sliding = false;
            SampleBackdrop();
        };
        BeginAnimation(LeftProperty, x);
        BeginAnimation(TopProperty, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    // --- fullscreen video -------------------------------------------------------------------------------

    /// <summary>
    /// Four times a second while the mini is on: moves it to another screen, or hides it, while a fullscreen window
    /// covers its screen, and puts it back when fullscreen ends.
    /// </summary>
    private void CheckFullscreen()
    {
        if (!_engine.Settings.MiniMonitor)
        {
            _fullscreenTimer.Stop();
            _fullscreen.Reset();
            return;
        }
        if (_dragging || _sliding) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var me = D.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var screens = Forms.Screen.AllScreens.Select(s => new ScreenArea(s.Bounds, s.WorkingArea, s.Primary)).ToList();
        var step = _fullscreen.Check(_watch.Check(), me, screens, _engine.Settings.MiniFullscreen, _engine.Settings.MiniFavorite);
        switch (step.Kind)
        {
            case FullscreenStepKind.Hide:
                Log.Write("mini hidden: a fullscreen window covers its screen");
                Hide();
                break;
            case FullscreenStepKind.Show:
                Log.Write("mini shown again: fullscreen ended");
                Show();
                SampleBackdrop();
                break;
            case FullscreenStepKind.MoveTo:
                Log.Write(_fullscreen.IsAway ? "mini moved to another screen: a fullscreen window covers its screen" : "mini moved back: fullscreen ended");
                MovePixels(hwnd, step.X, step.Y);
                SampleBackdrop();
                break;
        }
    }

    /// <summary>
    /// Moves to a spot in screen pixels. Another screen can have another scale, which resizes the mini as it
    /// arrives, so it is then kept inside that screen's work area.
    /// </summary>
    private static void MovePixels(IntPtr hwnd, int x, int y)
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (!GetWindowRect(hwnd, out var r)) return;
        var area = Forms.Screen.FromPoint(new D.Point(x, y)).WorkingArea;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int nx = Math.Max(area.Left, Math.Min(r.Left, area.Right - w)), ny = Math.Max(area.Top, Math.Min(r.Top, area.Bottom - h));
        if (nx != r.Left || ny != r.Top) SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // --- text colour from what is behind ---------------------------------------------------------------

    /// <summary>Picks white or black text for each gauge from the desktop behind it, seen through the panel.</summary>
    private void SampleBackdrop(double? panelLevel = null)
    {
        if (!IsVisible || TileList.ActualWidth < 1) return;
        try
        {
            Apply(_excluded ? CaptureBehindTiles() : CaptureAroundWindow(), panelLevel ?? _fill.Opacity);
        }
        catch (Exception e)
        {
            if (!_sampleFailureLogged) Log.Write($"mini monitor could not read the desktop: {e.Message}");
            _sampleFailureLogged = true;
        }
    }

    private void Apply(double[] columns, double panelLevel)
    {
        var bg = ((SolidColorBrush)Theme.Brush("Bg")).Color;
        double panel = Contrast.Luminance(bg.R, bg.G, bg.B);
        for (int i = 0; i < _tiles.Count && i < columns.Length; i++)
        {
            double behind = Contrast.Behind(columns[i], panel, panelLevel);
            _tiles[i].SetBackdrop(Contrast.WhiteText(behind, _tiles[i].OnDark));
        }
    }

    /// <summary>The desktop right behind the gauges. Works because this window is left out of captures.</summary>
    private double[] CaptureBehindTiles()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = TileList.PointToScreen(new Point(0, 0)); // device pixels
        return Capture(new D.Rectangle((int)origin.X, (int)origin.Y,
            (int)(TileList.ActualWidth * dpi.DpiScaleX), (int)(TileList.ActualHeight * dpi.DpiScaleY)));
    }

    /// <summary>Fallback: a strip just below the window (or above it, at a screen's bottom edge).</summary>
    private double[] CaptureAroundWindow()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = TileList.PointToScreen(new Point(0, 0));
        var window = PointToScreen(new Point(0, 0));
        int width = (int)(TileList.ActualWidth * dpi.DpiScaleX), height = 8;
        var below = new D.Rectangle((int)origin.X, (int)(window.Y + ActualHeight * dpi.DpiScaleY) + 2, width, height);
        bool onScreen = Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(below));
        return Capture(onScreen ? below : below with { Y = (int)window.Y - height - 2 });
    }

    private double[] Capture(D.Rectangle area)
    {
        using var bitmap = new D.Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bitmap))
            g.CopyFromScreen(area.Location, D.Point.Empty, bitmap.Size);
        var data = bitmap.LockBits(new D.Rectangle(D.Point.Empty, bitmap.Size), D.Imaging.ImageLockMode.ReadOnly,
            D.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return Contrast.ColumnLuminance(bytes, data.Width, data.Height, data.Stride, _tiles.Count);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // --- pictures for checking without the screen ------------------------------------------------------

    /// <summary>
    /// Saves the mini as it looks over a light and a dark striped stand-in desktop (path, and
    /// path with "-dark"), picking text colours from that backdrop exactly as it does from the real one.
    /// </summary>
    public void SaveImage(string path)
    {
        foreach (var (file, dark) in new[] { (path, false), (Path.ChangeExtension(path, null) + "-dark.png", true) })
            ImageExport.Write(RenderOver((w, h, dx, dy) => ImageExport.Backdrop(w, h, dx, dy, dark)), file);
        SampleBackdrop();
    }

    /// <summary>
    /// The mini over a picture of what is behind it (given its pixel size and dpi), with the
    /// gauge text coloured for that picture.
    /// </summary>
    public RenderTargetBitmap RenderOver(Func<int, int, double, double, BitmapSource> behind, double? pixelsPerInch = null)
    {
        PaintPanel();
        var probe = ImageExport.Render(this, LayoutWidth, solidBackground: false, pixelsPerInch);
        var backdrop = behind(probe.PixelWidth, probe.PixelHeight, probe.DpiX, probe.DpiY);
        if (TileList.Visibility == Visibility.Visible) // "lighthouse only" has no gauge text to colour
            Apply(ColumnsUnderTiles(backdrop), _engine.Settings.MiniBackgroundOpacity);
        return ImageExport.Over(backdrop, ImageExport.Render(this, LayoutWidth, solidBackground: false, pixelsPerInch));
    }

    private double[] ColumnsUnderTiles(BitmapSource backdrop)
    {
        var origin = TileList.TranslatePoint(new Point(0, 0), (Border)Content);
        double sx = backdrop.DpiX / 96, sy = backdrop.DpiY / 96;
        int x = (int)(origin.X * sx), y = (int)(origin.Y * sy);
        int w = Math.Min((int)(TileList.ActualWidth * sx), backdrop.PixelWidth - x);
        int h = Math.Min((int)(TileList.ActualHeight * sy), backdrop.PixelHeight - y);
        int stride = w * 4;
        var bytes = new byte[stride * h];
        backdrop.CopyPixels(new Int32Rect(x, y, w, h), bytes, stride, 0);
        return Contrast.ColumnLuminance(bytes, w, h, stride, _tiles.Count);
    }

    // --- the see-through panel -------------------------------------------------------------------------

    private void SetSeeThrough(double opacity)
    {
        _engine.Settings.MiniBackgroundOpacity = opacity;
        _engine.SaveSettings();
        FadeTo(opacity);
        MarkSeeThrough();
        SampleBackdrop(opacity);
    }

    private void MarkSeeThrough()
    {
        foreach (var item in SeeThroughItems)
            item.IsChecked = Math.Abs(double.Parse((string)item.Tag, CultureInfo.InvariantCulture)
                                      - _engine.Settings.MiniBackgroundOpacity) < 0.01;
    }

    /// <summary>The panel's fill in the theme's colour, at the see-through level, mouse or not. No outline.</summary>
    private void PaintPanel()
    {
        double level = _engine.Settings.MiniBackgroundOpacity;
        _fill = new SolidColorBrush(((SolidColorBrush)Theme.Brush("Bg")).Color) { Opacity = Math.Max(level, ClearButGrabbable) };
        Panel.Background = _fill;
    }

    private void FadeTo(double level)
    {
        var fade = new DoubleAnimation(Math.Max(level, ClearButGrabbable), TimeSpan.FromMilliseconds(180));
        Timeline.SetDesiredFrameRate(fade, 30);
        _fill.BeginAnimation(Brush.OpacityProperty, fade);
    }

    // --- placement -------------------------------------------------------------------------------------

    /// <summary>Opens at its favorite spot.</summary>
    public void ShowAtFavorite()
    {
        Left = -10000; // lay out off-screen first so it appears in place
        Show();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var (x, y) = Favorite((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi);
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
        UpdateLayout();
        SampleBackdrop();
        _fullscreen.Reset();
        _fullscreenTimer.Start();
    }

    /// <summary>
    /// The favorite spot's top-left in screen pixels, for a window this size: a named corner or edge of the main
    /// screen, or the custom spot ("Where it is now"). A custom spot on a screen that is gone falls back to bottom right.
    /// </summary>
    private (int X, int Y) Favorite(int width, int height, DpiScale dpi)
    {
        if (_engine.Settings is { MiniFavorite: "custom", MiniLeft: { } left, MiniTop: { } top } && OnAScreen(left, top, dpi))
            return ((int)(left * dpi.DpiScaleX), (int)(top * dpi.DpiScaleY));
        var area = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).WorkingArea;
        return MiniPlacement.Named(_engine.Settings.MiniFavorite, area.Left, area.Top, area.Right, area.Bottom, width, height)
               ?? MiniPlacement.Named("bottom-right", area.Left, area.Top, area.Right, area.Bottom, width, height)!.Value;
    }

    /// <summary>A corner or edge from the menu (it goes there now), or "custom": the spot it is at now.</summary>
    private void SetFavorite(string favorite)
    {
        _engine.Settings.MiniFavorite = favorite;
        if (favorite == "custom") (_engine.Settings.MiniLeft, _engine.Settings.MiniTop) = (Left, Top);
        _engine.SaveSettings();
        if (favorite == "custom") return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var me = WindowPixels(dpi);
        var (x, y) = Favorite(me.Width, me.Height, dpi);
        SlideTo(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    /// <summary>A monitor may have been unplugged since: keep the window where it can be seen and grabbed.</summary>
    private static bool OnAScreen(double left, double top, DpiScale dpi)
    {
        var grab = new D.Rectangle((int)(left * dpi.DpiScaleX), (int)(top * dpi.DpiScaleY), 60, 30);
        return Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(grab));
    }
}
