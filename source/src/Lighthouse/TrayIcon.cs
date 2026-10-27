using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lighthouse.Controls;
using Lighthouse.Core;
using Lighthouse.Core.Alerts;
using Lighthouse.Core.Autostart;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;
using Lighthouse.Core.Rendering;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Lighthouse;

/// <summary>The live icon on the taskbar (lamp over two bars), its tooltip, menu and toasts.</summary>
public sealed class TrayIcon : IDisposable
{
    private const string TaskFolder = @"\Lighthouse\";
    private const string TaskName = "Start at logon";

    private readonly MonitorEngine _engine;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon _icon = new();
    private readonly Forms.ToolStripMenuItem _pauseItem, _startupItem, _miniItem, _clickThroughItem;
    private readonly FlyoutViewModel _vm;
    private MiniWindow? _mini;
    private System.Drawing.Icon? _currentIcon;
    private IntPtr _currentHicon;
    private string _iconKey = "";
    private bool _taskbarLight = Theme.TaskbarLight;
    private FlyoutWindow? _flyout;
    private DateTime _flyoutHiddenAt = DateTime.MinValue;
    private DateTime _lampBlinkUntil = DateTime.MinValue;

    public TrayIcon(MonitorEngine engine)
    {
        _engine = engine;
        _vm = new FlyoutViewModel(engine, SetMini);
        _pauseItem = new Forms.ToolStripMenuItem("Pause alerts for 1 hour", null, (_, _) => TogglePause());
        _startupItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _miniItem = new Forms.ToolStripMenuItem("Mini monitor", null, (_, _) => SetMini(!_engine.Settings.MiniMonitor));
        // Here, not on the mini: a click-through mini can't be right-clicked to switch it back.
        _clickThroughItem = new Forms.ToolStripMenuItem("Clicks go through the mini monitor", null, (_, _) =>
        {
            _engine.Settings.MiniClickThrough = !_engine.Settings.MiniClickThrough;
            _engine.SaveSettings();
            _mini?.SetClickThrough(_engine.Settings.MiniClickThrough);
        });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open Lighthouse", null, (_, _) => ShowFlyout()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_miniItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add("History", null, (_, _) => ShowFlyout(history: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Task Manager", null, (_, _) => Open(SystemExe("Taskmgr.exe")));
        menu.Items.Add("Settings file", null, (_, _) => OpenSettingsFile());
        menu.Items.Add("Log file", null, (_, _) => Open(SystemExe("notepad.exe"), Paths.LogFile));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Lighthouse", null, (_, _) => Application.Current.Shutdown());
        menu.Opening += (_, _) => RefreshMenu();

        _icon.ContextMenuStrip = menu;
        _icon.Text = "Lighthouse";
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleFlyout();
        };
        _icon.BalloonTipClicked += (_, _) => ShowFlyout();
        SetIcon(0, Heat.Cool, 0, Heat.Cool);
        _icon.Visible = true;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        engine.Updated += s => _dispatcher.BeginInvoke(() => OnUpdate(s));
        engine.Notify += n => _dispatcher.BeginInvoke(() => ShowNotice(n));
        _ = PromoteOutOfOverflowAsync();
        if (engine.Settings.MiniMonitor) SetMini(true);
    }

    private void OnUpdate(Snapshot s)
    {
        // After a warning the lamp blinks for a few seconds, once a second with the sampler.
        bool lampLit = DateTime.Now >= _lampBlinkUntil || DateTime.Now.Second % 2 == 0;
        SetIcon(s.CpuLoad, TrayIconRenderer.CpuHeat(s.CpuTemp), s.GpuLoad, TrayIconRenderer.GpuHeat(s.GpuTemp, s.GpuHotspot), lampLit);
        _icon.Text = Tooltip(s);
        if (_flyout is { IsVisible: true } || _mini is { IsVisible: true }) Refresh(s);
    }

    /// <summary>One update of the shared view model feeds both the flyout and the mini monitor.</summary>
    private void Refresh(Snapshot s)
    {
        _vm.Update(s);
        if (_vm.Scene is not { } scene) return;
        _flyout?.UpdateScene(scene);
        _mini?.UpdateScene(scene);
    }

    /// <summary>Turns the always-on-top mini monitor on or off and remembers the choice.</summary>
    public void SetMini(bool on)
    {
        _engine.Settings.MiniMonitor = on;
        _engine.SaveSettings();
        if (on)
        {
            _mini ??= new MiniWindow(_vm, _engine, () => ShowFlyout(), () => SetMini(false));
            if (_engine.Latest is { } s) Refresh(s);
            _mini.ShowAtFavorite();
        }
        else
        {
            _mini?.Hide();
        }
        _vm.RaiseMini();
    }

    private void SetIcon(double cpu, Heat cpuHeat, double gpu, Heat gpuHeat, bool lampLit = true)
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        int bar = TrayIconRenderer.Layout.For(size).BarHeight;
        string key = $"{size}|{TrayIconRenderer.FillRows(cpu, bar)}|{cpuHeat}|{TrayIconRenderer.FillRows(gpu, bar)}|{gpuHeat}" +
                     $"|{_taskbarLight}|{lampLit}";
        if (key == _iconKey) return; // nothing visible changed
        _iconKey = key;

        IntPtr hicon = TrayIconRenderer.RenderHicon(size, cpu, cpuHeat, gpu, gpuHeat, _taskbarLight, lampLit);
        var icon = System.Drawing.Icon.FromHandle(hicon);
        _icon.Icon = icon; // the shell keeps its own copy
        _currentIcon?.Dispose();
        TrayIconRenderer.Destroy(_currentHicon);
        _currentIcon = icon;
        _currentHicon = hicon;
    }

    internal static string Tooltip(Snapshot s)
    {
        string cpu = s.CpuTemp is { } ct ? $"CPU {s.CpuLoad:0}% · {ct:0}°C" : $"CPU {s.CpuLoad:0}%";
        string gpu = s.GpuTemp is { } gt ? $"GPU {s.GpuLoad:0}% · {gt:0}°C" : $"GPU {s.GpuLoad:0}%";
        string mem = $"RAM {s.RamUsedGb:0}/{s.RamTotalGb:0} GB · VRAM {s.VramUsedGb:0}/{s.VramTotalGb:0} GB";
        string text = $"Lighthouse\n{cpu} | {gpu}\n{mem}";
        if (s.Game is { } game) text += $"\n{game.Label}";
        return text.Length <= 127 ? text : text[..127];
    }

    private void ShowNotice(Notice n)
    {
        var icon = n.Severity switch
        {
            Severity.Critical => Forms.ToolTipIcon.Error,
            Severity.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(8000, n.Title, n.Message, icon);
        if (n.Severity >= Severity.Warning) _lampBlinkUntil = DateTime.Now.AddSeconds(8);
    }

    public void ToggleFlyout()
    {
        if (_flyout is { IsVisible: true })
        {
            _flyout.Hide();
            return;
        }
        // The click that closed the flyout (by taking focus) must not reopen it.
        if (DateTime.Now - _flyoutHiddenAt < TimeSpan.FromMilliseconds(350)) return;
        ShowFlyout();
    }

    /// <summary>Opens the flyout on its main page, or on History (the tray menu's History).</summary>
    public void ShowFlyout(bool history = false)
    {
        if (_flyout is null)
        {
            _flyout = new FlyoutWindow(_vm);
            _flyout.Hidden += () => _flyoutHiddenAt = DateTime.Now;
        }
        _engine.ReloadSettings();
        if (_engine.Latest is { } s) Refresh(s);
        if (history) _vm.ShowHistory();
        else _vm.ShowMain();
        _flyout.ShowNearTray();
    }

    /// <summary>Presses the flyout's Speed test button (the --run-speed-test signal), through the same command.</summary>
    public void RunSpeedTest()
    {
        Log.Write("speed test requested by signal");
        if (_vm.CanSpeedTest) _vm.SpeedTestCommand.Execute(null);
    }

    /// <summary>
    /// Saves the flyout and the mini monitor, as they would look now, to flyout.png and mini.png next to the
    /// given path, without opening either. Logs how many gauges exist, which must stay flat between renders.
    /// </summary>
    public void RenderFlyout(string path)
    {
        try
        {
            _flyout ??= new FlyoutWindow(_vm);
            _mini ??= new MiniWindow(_vm, _engine, () => ShowFlyout(), () => SetMini(false));
            Gauge.Animate = false; // the picture should show the values, not a gauge mid-sweep
            try
            {
                if (_engine.Latest is { } s) Refresh(s);
                _flyout.SaveImage(path);
                var clipped = TextFit.Clipped(_flyout);
                _mini.SaveImage(Path.Combine(Path.GetDirectoryName(path)!, "mini.png"));
                clipped.AddRange(TextFit.Clipped(_mini).Select(c => "mini: " + c));
                Log.Write(clipped.Count == 0 ? "text check: nothing clipped" : "text check: CLIPPED " + string.Join("; ", clipped));
            }
            finally
            {
                Gauge.Animate = true;
            }
            Log.Write($"flyout and mini rendered to {Path.GetDirectoryName(path)}; gauges created so far: {Gauge.Created}");
        }
        catch (Exception e)
        {
            Log.Write($"render failed: {e}");
        }
    }

    private void RefreshMenu()
    {
        bool paused = _engine.Settings.AlertsPausedUntil is { } until && until > DateTime.Now;
        _pauseItem.Text = paused ? $"Resume alerts (paused until {_engine.Settings.AlertsPausedUntil:t})" : "Pause alerts for 1 hour";
        _miniItem.Checked = _engine.Settings.MiniMonitor;
        _clickThroughItem.Checked = _engine.Settings.MiniClickThrough;
        _clickThroughItem.Visible = _engine.Settings.MiniMonitor;
        bool? enabled = ScheduledTasks.IsEnabled(TaskFolder, TaskName);
        _startupItem.Checked = enabled == true;
        _startupItem.Enabled = enabled is not null;
        if (enabled is null) _startupItem.Text = "Start with Windows (run Install.cmd)";
    }

    private void TogglePause()
    {
        bool paused = _engine.Settings.AlertsPausedUntil is { } until && until > DateTime.Now;
        if (paused)
        {
            _engine.Settings.AlertsPausedUntil = null;
            _engine.SaveSettings();
        }
        else
        {
            _engine.PauseAlerts(TimeSpan.FromHours(1));
        }
    }

    private void ToggleStartup()
    {
        // The logon tasks (the app and its sensor helper) are changed only with admin rights, so this asks for them:
        // Windows shows its UAC prompt for this one change.
        bool enabled = ScheduledTasks.IsEnabled(TaskFolder, TaskName) == true;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--set-startup {(enabled ? "off" : "on")}")
                { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // The UAC prompt was cancelled: nothing changes.
        }
        catch (Exception e)
        {
            Log.Write($"startup toggle failed: {e.Message}");
        }
    }

    private void OpenSettingsFile()
    {
        if (!File.Exists(Paths.SettingsFile)) _engine.SaveSettings();
        Open(SystemExe("notepad.exe"), Paths.SettingsFile);
    }

    /// <summary>A program from System32 by its full path, never one found through PATH or App Paths (which you can change).</summary>
    private static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>
    /// Starts a program with your own rights, not Lighthouse's admin ones: an elevated Notepad's Open dialog can open
    /// and run anything as admin.
    /// </summary>
    private static void Open(string exe, string? arg = null)
    {
        try
        {
            UserContext.Start(exe, arg is null ? "" : $"\"{arg}\"");
        }
        catch (Exception e)
        {
            Log.Write($"could not open {exe}: {e.Message}");
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        _dispatcher.BeginInvoke(() =>
        {
            _taskbarLight = Theme.TaskbarLight;
            _iconKey = "";
            Theme.Apply();
        });
    }

    /// <summary>
    /// Windows 11 puts new tray icons in the overflow. Once per exe path, move Lighthouse onto the taskbar
    /// (HKCU\Control Panel\NotifyIconSettings\*\IsPromoted). If you hide it later, that sticks.
    /// </summary>
    private async Task PromoteOutOfOverflowAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || string.Equals(_engine.Settings.PromotedExe, exe, StringComparison.OrdinalIgnoreCase)) return;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                foreach (string id in root?.GetSubKeyNames() ?? [])
                {
                    using var entry = Registry.CurrentUser.OpenSubKey($@"Control Panel\NotifyIconSettings\{id}", writable: true);
                    // Windows writes some paths with a known folder in front ("{6D809377-...}\Lighthouse\Lighthouse.exe").
                    if (entry?.GetValue("ExecutablePath") is not string path || !KnownFolders.SamePath(path, exe)) continue;
                    entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    _engine.Settings.PromotedExe = exe;
                    _engine.SaveSettings();
                    Log.Write($"tray icon promoted to the taskbar ({id})");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Write($"promote failed: {e.Message}");
                return;
            }
        }
        Log.Write("tray icon not promoted: Windows has no entry for this exe yet");
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        TrayIconRenderer.Destroy(_currentHicon);
        _flyout?.Close();
        _mini?.Close();
    }
}
