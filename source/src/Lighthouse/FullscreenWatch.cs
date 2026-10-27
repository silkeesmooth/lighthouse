using System.Runtime.InteropServices;
using System.Text;
using D = System.Drawing;
using Forms = System.Windows.Forms;

namespace Lighthouse;

/// <summary>
/// Which screen, if any, a fullscreen window covers: a video in a browser (YouTube's fullscreen in Chrome or Edge,
/// F11), a film player, a borderless or exclusive game, a slideshow. A window counts when it covers its whole monitor,
/// taskbar too, and isn't simply maximized with its title bar (a browser stays maximized under its fullscreen video,
/// with the title bar gone; a maximized window over an auto-hidden taskbar keeps it). The window in front is checked; once one is fullscreen it keeps counting
/// while the window in front is on another screen (or is Lighthouse), so clicking on a second screen doesn't bring the
/// mini back over the video. Exclusive fullscreen that hides its window from this test still shows in Windows' own
/// "do not disturb" state.
/// </summary>
internal sealed class FullscreenWatch
{
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private IntPtr _last;

    /// <summary>The covered monitor's bounds (device pixels), or null.</summary>
    public D.Rectangle? Check()
    {
        IntPtr front = GetForegroundWindow();
        if (Covers(front) is { } screen)
        {
            _last = front;
            return screen;
        }
        if (_last != IntPtr.Zero && Covers(_last) is { } still
            && (front == IntPtr.Zero || Ours(front) || MonitorFromWindow(front, 2) != MonitorFromWindow(_last, 2)))
            return still;
        _last = IntPtr.Zero;
        if (SHQueryUserNotificationState(out int state) == 0 && state == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */)
        {
            var primary = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            return primary.Bounds;
        }
        return null;
    }

    /// <summary>The monitor a window covers completely, or null (not visible, minimized, maximized with a title bar, the desktop, ours).</summary>
    private static D.Rectangle? Covers(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || Ours(hwnd)) return null;
        const long WS_CAPTION = 0x00C00000;
        if (IsZoomed(hwnd) && ((long)GetWindowLongPtr(hwnd, -16 /* GWL_STYLE */) & WS_CAPTION) == WS_CAPTION) return null;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        if (ShellClasses.Contains(name.ToString())) return null;
        if (!GetWindowRect(hwnd, out var r)) return null;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) return null;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom
            ? D.Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom)
            : null;
    }

    private static bool Ours(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
