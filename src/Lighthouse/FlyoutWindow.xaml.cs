using System.Windows;
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
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) Hidden?.Invoke();
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
