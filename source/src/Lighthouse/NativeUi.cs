using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lighthouse;

internal static class NativeUi
{
    /// <summary>
    /// Explorer runs at medium integrity and the logon task runs Lighthouse elevated, so UIPI would drop the
    /// tray icon's click messages and the "taskbar restarted" broadcast. Let exactly those two through.
    /// </summary>
    public static void AllowTrayMessagesWhenElevated()
    {
        const uint WM_USER = 0x0400;
        ChangeWindowMessageFilter(WM_USER + 1024, 1 /* MSGFLT_ADD */); // WinForms NotifyIcon's callback message
        ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), 1);
    }

    /// <summary>Windows 11 rounded corners and shadow for a borderless window.</summary>
    public static void RoundCorners(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        int preference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
    }

    /// <summary>
    /// Leaves the window out of screen captures (Windows 10 2004+), so the mini monitor can capture the desktop
    /// behind itself to pick its text colour. It also keeps it out of screenshots and screen shares.
    /// </summary>
    public static bool ExcludeFromCapture(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        return SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
