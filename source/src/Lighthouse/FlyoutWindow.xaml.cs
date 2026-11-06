using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using Lighthouse.Controls;
using Forms = System.Windows.Forms;

namespace Lighthouse;

/// <summary>The panel that opens above the tray icon, like Windows' own flyouts.</summary>
public partial class FlyoutWindow : Window
{
    private double _bottom;

    public event Action? Hidden;

    /// <param name="vm">Shared with the mini monitor, so both show the same numbers.</param>
    public FlyoutWindow(FlyoutViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        NativeUi.RoundCorners(this);
        Deactivated += (_, _) => Hide();
        // History keeps the flyout's size: the same panel turning a page, its list filling what is left.
        MainPage.SizeChanged += (_, e) =>
        {
            if (MainPage.Visibility == Visibility.Visible && e.NewSize.Height > 0) HistoryPage.Height = Math.Max(480, e.NewSize.Height - 12);
        };
        // A label that wraps to two lines can make "Using it now" taller, here or in the other view, but it doesn't
        // shrink back while open: the panel keeps one size instead of bouncing every second or on a switch of view.
        UsingSection.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height > UsingSection.MinHeight) UsingSection.MinHeight = e.NewSize.Height;
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            UsingSection.MinHeight = 0;
            Hidden?.Invoke();
        };
        // Grows upward: the bottom edge stays just above the taskbar as rows come and go.
        SizeChanged += (_, _) =>
        {
            if (_bottom > 0) Top = _bottom - ActualHeight;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (DataContext is FlyoutViewModel { HistoryOpen: true } history) history.ShowMain(); // Escape goes back first
            else Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepBottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// When the panel grows or shrinks (the speed test opening, a card coming), moves its top edge in the same step,
    /// so the bottom stays put. Moving it afterwards drew one frame hanging below its spot, then jumped.
    /// </summary>
    private IntPtr KeepBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
        if (msg != WM_WINDOWPOSCHANGING || _bottom <= 0 || !IsVisible) return IntPtr.Zero;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_NOSIZE) != 0 || !GetWindowRect(hwnd, out var r) || pos.cy == r.Bottom - r.Top) return IntPtr.Zero;
        if ((pos.flags & SWP_NOMOVE) != 0) (pos.x, pos.flags) = (r.Left, pos.flags & ~SWP_NOMOVE);
        pos.y = r.Bottom - pos.cy; // the bottom edge, where it is now
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    public void UpdateScene(SceneState scene) => Scene.Update(scene);

    public void SaveImage(string path) => ImageExport.Save(this, Width, path);

    public void ShowNearTray()
    {
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        Left = -10000; // measure off-screen first so the first frame lands in place
        Show();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        double right = area.Right / dpi.DpiScaleX, bottom = area.Bottom / dpi.DpiScaleY;
        _bottom = bottom - 12;
        Left = right - ActualWidth - 12;
        Top = _bottom - ActualHeight;
        Activate();
    }
}
