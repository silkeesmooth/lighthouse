using System.IO;
using System.Text.Json;
using System.Windows;
using Lighthouse.Core;
using Lighthouse.Core.Health;
using Lighthouse.Core.Sensors;
using Lighthouse.Core.Workloads;

namespace Lighthouse;

public partial class App : Application
{
    private const string MutexName = @"Local\Lighthouse.Tray";
    private const string ShowEventName = @"Local\Lighthouse.Show";
    private const string ExitEventName = @"Local\Lighthouse.Exit";
    private const string RenderEventName = @"Local\Lighthouse.Render";
    private const string ReloadEventName = @"Local\Lighthouse.Reload";
    private const string SpeedTestEventName = @"Local\Lighthouse.SpeedTest";

    private Mutex? _single;
    private EventWaitHandle? _showSignal, _exitSignal, _renderSignal, _reloadSignal, _speedTestSignal;
    private MonitorEngine? _engine;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"ui error: {ex.Exception}");
            ex.Handled = true;
        };

        // The sensor helper: the only part of Lighthouse that runs as admin. No window, no files, no settings.
        if (e.Args is ["--sensors", ..])
        {
            Task.Run(() => SensorHost.Run(CancellationToken.None))
                .ContinueWith(t => Dispatcher.BeginInvoke(() => Shutdown(t.IsCompletedSuccessfully ? t.Result : 1)));
            return;
        }

        // Diagnostics for verification: write what the sensors see and exit.
        if (e.Args is ["--dump-sensors", var dumpPath, ..])
        {
            RunAndExit(() => DumpSensors(dumpPath));
            return;
        }
        if (e.Args is ["--preview", var previewDir, ..])
        {
            RunAndExit(() => Preview.Write(previewDir));
            return;
        }
        if (e.Args is ["--gpu-idle", var idlePath, var idleSeconds, ..])
        {
            RunAndExit(() => GpuIdle(idlePath, int.Parse(idleSeconds), e.Args.Length > 3 ? e.Args[3] : ""));
            return;
        }
        if (e.Args is ["--speed-test", var speedPath, ..])
        {
            // Off the UI thread: the test's awaits must not wait for a dispatcher this thread is blocking.
            RunAndExit(() => File.WriteAllText(speedPath, Task.Run(SpeedTestReport).GetAwaiter().GetResult()));
            return;
        }
        if (e.Args is ["--readme-images", var imagesDir, ..])
        {
            RunAndExit(() => ReadmeImages.Write(imagesDir));
            return;
        }
        if (e.Args is ["--history-image", var historyDb, var historyPng, ..])
        {
            RunAndExit(() => Preview.HistoryImage(historyDb, historyPng));
            return;
        }
        if (e.Args is ["--text-check", var checkPath, ..])
        {
            RunAndExit(() => Preview.TextCheck(checkPath));
            return;
        }
        if (e.Args is ["--measure", var measurePath, ..])
        {
            _ = MeasureAndExit(measurePath);
            return;
        }
        if (e.Args is ["--snapshot", var snapPath, ..])
        {
            int seconds = e.Args.Length > 2 && int.TryParse(e.Args[2], out int s) ? s : 6;
            RunAndExit(() => WriteSnapshot(snapPath, seconds));
            return;
        }

        // Signals for the running copy: --exit (used by tools\update.ps1), --render (saves the flyout as flyout.png),
        // --reload (re-reads settings.json after a hand edit), --run-speed-test (presses the flyout's Speed test button).
        if (e.Args is [("--exit" or "--render" or "--reload" or "--run-speed-test") and var signal, ..])
        {
            Signal(signal switch
            {
                "--exit" => ExitEventName,
                "--render" => RenderEventName,
                "--run-speed-test" => SpeedTestEventName,
                _ => ReloadEventName,
            });
            Shutdown();
            return;
        }

        // --dismiss <check id>: hide a health check for good (ram-speed, gpu-idle-power, ...).
        if (e.Args is ["--dismiss", var healthId, ..])
        {
            RunAndExit(() =>
            {
                var settings = Settings.Load();
                if (!settings.DismissedHealth.Contains(healthId)) settings.DismissedHealth.Add(healthId);
                settings.Save();
                Signal(ReloadEventName);
                Log.Write($"health check '{healthId}' dismissed from the command line");
            });
            return;
        }

        // --set-startup on|off: switch both logon tasks (the app and its sensor helper). Needs admin; the tray menu's
        // "Start with Windows" starts this through a UAC prompt.
        if (e.Args is ["--set-startup", ("on" or "off") and var state, ..])
        {
            RunAndExit(() =>
            {
                foreach (string task in new[] { "Start at logon", "Sensors" })
                    if (Core.Autostart.ScheduledTasks.IsEnabled(@"\Lighthouse\", task) is not null)
                        Core.Autostart.ScheduledTasks.SetEnabled(@"\Lighthouse\", task, state == "on");
                Log.Write($"start with Windows: {state}");
            });
            return;
        }

        // --import <file>: copy every setting the file names into settings.json and tell the running copy to reload
        // (see examples\settings.example.json). Settings the file doesn't name are kept.
        if (e.Args is ["--import", var importPath, ..])
        {
            RunAndExit(() =>
            {
                var (merged, imported, unknown) = Settings.Load().Merge(File.ReadAllText(importPath));
                _ = new JobMatcher(merged.Jobs); // logs any job pattern that doesn't compile
                merged.Save();
                Signal(ReloadEventName);
                Log.Write($"imported {string.Join(", ", imported)} from {importPath}" +
                          (unknown.Count > 0 ? $"; ignored unknown settings: {string.Join(", ", unknown)}" : ""));
            });
            return;
        }

        if (!TryBecomeTheInstance())
        {
            Shutdown();
            return;
        }

        // Normally the app runs with your own rights and this doesn't arise. Started as admin by hand, it writes into
        // its data folder as admin, and a folder swapped for a link would send those writes anywhere.
        if (Core.Native.UserContext.Elevated && Paths.LockDataDir() is { } unsafeFolder)
        {
            MessageBox.Show($"Lighthouse didn't start: {unsafeFolder}. It was started as admin, so it won't write through a link. " +
                            "Remove the link and start Lighthouse again.", "Lighthouse", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        NativeUi.AllowTrayMessagesWhenElevated();
        var settings = Settings.Load();
        Theme.Chosen = settings.Theme switch { "light" => true, "dark" => false, _ => null };
        Theme.Apply();
        _engine = new MonitorEngine(settings);
        _tray = new TrayIcon(_engine);
        _engine.Start();
        Log.Write($"Lighthouse {typeof(App).Assembly.GetName().Version} up (pid {Environment.ProcessId}); {Core.Native.UserContext.Describe()}");

        ThreadPool.RegisterWaitForSingleObject(_showSignal!, (_, _) => Dispatcher.BeginInvoke(() => _tray?.ShowFlyout()),
            null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_exitSignal!, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Log.Write("exit requested");
            Shutdown();
        }), null, Timeout.Infinite, executeOnlyOnce: true);
        ThreadPool.RegisterWaitForSingleObject(_renderSignal!, (_, _) => Dispatcher.BeginInvoke(() =>
            _tray?.RenderFlyout(Path.Combine(Paths.DataDir, "flyout.png"))), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_reloadSignal!, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _engine?.ReloadSettings();
            Log.Write("settings reloaded");
        }), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_speedTestSignal!, (_, _) => Dispatcher.BeginInvoke(() => _tray?.RunSpeedTest()),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void Signal(string name)
    {
        if (EventWaitHandle.TryOpenExisting(name, out var handle)) handle.Set();
    }

    private bool TryBecomeTheInstance()
    {
        // Already running (elevated or not): a second launch just opens its flyout.
        bool running;
        try
        {
            running = Mutex.TryOpenExisting(MutexName, out var existing);
            existing?.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            running = true;
        }
        if (running)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            return false;
        }

        // Plain named objects: a non-admin launch can still signal the elevated copy (tools\update.ps1 relies on it).
        _single = new Mutex(true, MutexName);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _renderSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RenderEventName);
        _reloadSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEventName);
        _speedTestSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SpeedTestEventName);
        return true;
    }

    private async Task MeasureAndExit(string path)
    {
        try
        {
            await Preview.Measure(path);
        }
        catch (Exception ex)
        {
            Log.Write($"measure failed: {ex}");
        }
        Shutdown();
    }

    private void RunAndExit(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"diagnostic failed: {ex}");
        }
        Shutdown();
    }

    private static void DumpSensors(string path)
    {
        var gpu = Dxgi.MainAdapter();
        using var hw = new HardwareSampler(gpu?.Name);
        var topo = CpuTopology.Read();
        string text = $"dxgi: {gpu?.Name} {gpu?.VramGb:0.0} GB {gpu?.PdhLuid}{Environment.NewLine}" +
                      $"ccds: {string.Join(" | ", topo.Ccds.Select(c => $"#{c.Index} L3 {c.L3Bytes >> 20} MB cpus {c.Processors.First()}-{c.Processors.Last()}"))}" +
                      $" cache={topo.CacheCcd?.Index}{Environment.NewLine}" + hw.Dump();
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// `--gpu-idle <file> <seconds> [label]`: the GPU's median power, memory clock and load over that many seconds,
    /// appended as one line. Used to compare display setups for idle power.
    /// </summary>
    /// <summary>`--speed-test <file>`: one speed test, with every progress report, for checking the numbers.</summary>
    private static async Task<string> SpeedTestReport()
    {
        var lines = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new InlineProgress<Core.Network.SpeedProgress>(p =>
        {
            lock (lines) lines.Add($"{sw.Elapsed.TotalSeconds,5:0.0}s {p.Phase} {(p.Mbps is { } m ? Core.Network.SpeedTest.Format(m) : "")}");
        });
        Core.Network.SpeedTest.Trace = line =>
        {
            lock (lines) lines.Add($"{sw.Elapsed.TotalSeconds,5:0.0}s   {line}");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await Core.Network.SpeedTest.RunAsync(progress, cts.Token);
        lines.Add($"{sw.Elapsed.TotalSeconds,5:0.0}s done: {r.Summary} {r.Detail}");
        lines.Add($"raw: ping {r.PingMs:0.0} ms, down {r.DownMbps:0.0} Mbps, up {r.UpMbps:0.0} Mbps, {r.BytesUsed} bytes");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>An IProgress that reports on the caller's thread (Progress&lt;T&gt; would post to a sync context).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void GpuIdle(string path, int seconds, string label)
    {
        var gpu = Dxgi.MainAdapter();
        using var hw = new HardwareSampler(gpu?.Name);
        using var counters = new CounterSampler(gpu);
        var power = new List<double>();
        var clock = new List<double>();
        var load = new List<double>();
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var reading = hw.Sample();
            load.Add(counters.Sample().GpuLoad);
            if (reading.GpuPower is { } p) power.Add(p);
            if (reading.GpuMemClockMhz is { } m) clock.Add(m);
        }
        static string Median(List<double> v) => v.Count == 0 ? "-" : $"{v.Order().ElementAt(v.Count / 2):0}";
        File.AppendAllText(path, $"{label,-14} power {Median(power),3} W (min {(power.Count > 0 ? power.Min() : 0):0}), " +
                                 $"memory clock {Median(clock),4} MHz (min {(clock.Count > 0 ? clock.Min() : 0):0}), " +
                                 $"GPU load {(load.Count > 0 ? load.Average() : 0):0.0}%{Environment.NewLine}");
    }

    /// <summary>Samples for a few seconds and writes the snapshot as JSON. History stays in memory: never the real file.</summary>
    private static void WriteSnapshot(string path, int seconds)
    {
        using var engine = new MonitorEngine(Settings.Load(), diagnostic: true);
        engine.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var snapshot = engine.Latest;
        var report = new
        {
            engine.Machine.Line,
            Snapshot = snapshot,
            Headline = snapshot is null ? null : Headline.Build(snapshot, engine.ActiveAlerts, DateTime.Now),
            Health = HealthChecks.RunAll(null, DateTime.Now.AddDays(-7)),
            Llm = engine.Llm is { } l ? new { l.Pid, Kind = l.AppName, l.LocalBaseUrl, l.ModelShort } : null,
            // What the startup guard keeps off: the administrator's saved list, parsed as the running copy parses it.
            AutostartGuard = Core.Autostart.AutostartList.Read().Select(e => e.ToString()).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, Settings.JsonOptions));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();
        _single?.Dispose();
        _showSignal?.Dispose();
        _exitSignal?.Dispose();
        _renderSignal?.Dispose();
        _reloadSignal?.Dispose();
        _speedTestSignal?.Dispose();
        base.OnExit(e);
    }
}
