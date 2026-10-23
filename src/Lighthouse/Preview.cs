using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lighthouse.Controls;
using Lighthouse.Core;
using Lighthouse.Core.Model;
using Lighthouse.Core.Workloads;
using Lighthouse.Core.Rendering;
using D = System.Drawing;

namespace Lighthouse;

/// <summary>
/// `Lighthouse.exe --preview <dir>`: draws the scene in set situations and the tray icon zoomed in, as PNGs,
/// so the look can be checked without opening anything on screen.
/// </summary>
internal static class Preview
{
    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        Theme.Apply();
        var scenes = new (string Name, int Hour, int Minute, double Step, SceneState State)[]
        {
            ("1-day-calm-llm-idle", 13, 0, 0.2, new(4, Heat.Cool, 2, 0.32, [], null, Moon: true, MoonThinking: false)),
            ("2-dusk-two-job-runs", 19, 20, 1.1,
                new(45, Heat.Warm, 10, 0.55, [new("a", "load west", 30), new("b", "verify south", 12)], null, false, false)),
            ("3-night-battlefield", 23, 30, 0.4, new(70, Heat.Warm, 97, 0.6, [], "Battlefield 6", true, false)),
            ("4-night-llm-thinking", 2, 0, 2.6, new(12, Heat.Cool, 99, 0.7, [], null, true, true)),
            ("5-afternoon-hot-cpu", 15, 0, 0.05, new(98, Heat.Hot, 40, 0.92, [new("c", "load north", 70)], null, false, false)),
            ("8-day-speed-test-download", 13, 0, 0.6, new(6, Heat.Cool, 3, 0.3, [], null, false, false,
                new SceneSignal(SignalPhase.Download, 312, "↓ 312 Mbps"))),
            ("9-night-speed-test-upload", 22, 0, 0.6, new(6, Heat.Cool, 3, 0.3, [], null, true, false,
                new SceneSignal(SignalPhase.Upload, 18, "↑ 18 Mbps"))),
            ("10-dusk-speed-test-ping", 19, 20, 0.5, new(6, Heat.Cool, 3, 0.3, [], null, false, false,
                new SceneSignal(SignalPhase.Ping, null, "Ping…"))),
            ("11-day-storm-warning", 14, 0, 3, new(60, Heat.Warm, 40, 0.5, [], null, false, false,
                Weather: new SceneWeather(Storm: true, Lightning: false, Fog: 0))),
            ("12-night-lightning-critical", 23, 0, 0.05, new(95, Heat.Hot, 60, 0.6, [], null, false, false,
                Weather: new SceneWeather(Storm: true, Lightning: true, Fog: 0))),
            ("13-dusk-fog-memory-full", 19, 0, 4, new(30, Heat.Cool, 20, 0.95, [], null, false, false,
                Weather: new SceneWeather(Storm: false, Lightning: false, Fog: 0.8))),
            ("14-afternoon-heat-haze", 15, 30, 4, new(97, Heat.Hot, 30, 0.4, [], null, false, false)),
            ("15-day-gulls-in-foam", 11, 0, 4, new(20, Heat.Cool, 10, 0.4, [], null, false, false,
                NetDownMbps: 300, DiskActivity: 0.9)),
            ("16-evening-gulls-out", 18, 0, 4, new(20, Heat.Cool, 10, 0.4, [], null, false, false,
                NetUpMbps: 40)),
        };
        foreach (var (name, hour, minute, step, state) in scenes)
        {
            var scene = new HarbourScene { Clock = DateTime.Today.AddHours(hour).AddMinutes(minute) };
            scene.Update(state);
            scene.Step(step);
            Arrange(scene, 394, 150); // drawing happens in layout, so data first
            Save(scene, 394, 150, Path.Combine(dir, name + ".png"));
        }

        var gauges = new StackPanelOfGauges();
        Arrange(gauges.Root, 394, 80);
        Save(gauges.Root, 394, 80, Path.Combine(dir, "6-gauges.png"));

        TrayIcons(Path.Combine(dir, "7-tray-icons.png"));
    }

    /// <summary>
    /// `Lighthouse.exe --measure <file>`: what the animations cost. Three 15 s phases of this process's own CPU
    /// (sampling only, plus the mini monitor, plus the flyout), with both windows placed off-screen at -10000
    /// so nothing appears or takes focus.
    /// </summary>
    public static async Task Measure(string path)
    {
        Theme.Apply();
        using var engine = new MonitorEngine(Settings.Load(), diagnostic: true);
        var vm = new FlyoutViewModel(engine, _ => { });
        MiniWindow? mini = null;
        FlyoutWindow? flyout = null;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        engine.Updated += s => dispatcher.BeginInvoke(() =>
        {
            if (mini is null && flyout is null) return;
            vm.Update(s);
            if (vm.Scene is { } scene)
            {
                mini?.UpdateScene(scene);
                flyout?.UpdateScene(scene);
            }
        });
        engine.Start();
        await Task.Delay(4000); // let sensors settle

        async Task<double> Phase()
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu0 = process.TotalProcessorTime;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(12000);
            process.Refresh();
            return (process.TotalProcessorTime - cpu0).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        }

        var results = new List<(string Name, double Pct)> { ("sampling only", await Phase()) };
        mini = new MiniWindow(vm, engine, () => { }, () => { }) { Left = -10000, Top = -10000 };
        foreach (var (name, fps, animate) in new[] { ("mini, held still", 12.0, false), ("mini at 12 fps", 12.0, true), ("mini at 4 fps", 4.0, true) })
        {
            mini.Scene.FramesPerSecond = fps;
            mini.Scene.AnimationEnabled = animate;
            mini.Show();
            await Task.Delay(3000); // first-show layout and JIT are not the running cost
            results.Add((name, await Phase()));
            mini.Hide();
        }
        flyout = new FlyoutWindow(vm) { Left = -10000, Top = -10000, ShowActivated = false };
        flyout.Show();
        await Task.Delay(3000);
        results.Add(("flyout at 30 fps", await Phase()));
        flyout.Hide();

        int cores = Environment.ProcessorCount;
        File.WriteAllLines(path, results.Select(r =>
            $"{r.Name,-18} {r.Pct,6:0.0}% of one core = {r.Pct / cores,5:0.00}% of the machine (+{(r.Pct - results[0].Pct) / cores:0.00}%)"));
    }

    /// <summary>
    /// `Lighthouse.exe --text-check <file>`: fills the flyout and the mini monitor with worst-case values (the
    /// longest labels, three-digit temperatures, 100% everywhere, the "no admin" note) and lists any text that
    /// would be cut off. An empty list is the pass.
    /// </summary>
    public static void TextCheck(string path)
    {
        Theme.Apply();
        Gauge.Animate = false; // the picture shows gauges at their values
        using var engine = new MonitorEngine(Settings.Load(), diagnostic: true);
        var vm = new FlyoutViewModel(engine, _ => { });
        WorkloadUsage W(string label, WorkloadKind kind, DateTime? started = null) => new(label, label, kind, 100, 100, 63.9, 19.9, 999,
            Started: started, DiskMBps: 9999, NetDownMbps: 9999, NetUpMbps: 9999);
        string pipeline = "Data pipeline: " + JobText.DefaultLabel("python -m pipeline load --region west --run --from transform:dedupe --max-rows 400000");
        // Two long past runs, so the job card shows its longest time-left line.
        for (int i = 1; i <= 2; i++)
            engine.History.AddJobRun(new Core.History.JobRun("Data pipeline", "load", pipeline, DateTime.Now.AddDays(-i),
                DateTime.Now.AddDays(-i).AddHours(23).AddMinutes(59), 0));
        engine.ShowDigest(
        [
            "Gaming: 99h 59m across 99 sessions, most of it Microsoft Flight Simulator 2024 (99h 59m).",
            "Peaks: CPU 105°C, GPU 110°C (hotspot 110°C).",
            "Data pipeline: 999 runs, 999 stopped (exit -1073741510, exit 4, no exit code).",
            "Internet: 9999 to 9999 Mbps down across 999 tests.",
            "Alerts: 999 (999 cpu is very hot, 999 memory is nearly full, 999 ssd is hot).",
        ]);
        var worst = new Snapshot
        {
            CpuLoad = 100, CpuTemp = 105, CpuPower = 230, CpuClockMhz = 5750,
            GpuLoad = 100, GpuTemp = 110, GpuHotspot = 110, GpuPower = 335,
            VramUsedGb = 19.9, VramTotalGb = 20, RamUsedGb = 63.9, RamTotalGb = 64, CommitPct = 100,
            DimmTemp = 85, NvmeTemp = 84, FullSensors = false,
            Workloads =
            [
                W(pipeline, WorkloadKind.Job, DateTime.Now.AddMinutes(-1)),
                W("Microsoft Flight Simulator 2024", WorkloadKind.Game),
                W("Local LLM · Qwen3-30B-A3B-Instruct", WorkloadKind.LocalLlm),
                W("Claude Code (12)", WorkloadKind.ClaudeCode),
                W("Windows Update", WorkloadKind.Windows),
                W("Pipeline review app: 8000", WorkloadKind.Service),
                W("Chrome", WorkloadKind.App),
            ],
            Rest = W("Everything else", WorkloadKind.Windows),
            Self = W("Lighthouse", WorkloadKind.Lighthouse),
            DiskMBps = 9999, NetDownBytesPerSec = 1.25e9, NetUpBytesPerSec = 1.25e9, IoTraced = false,
        };
        worst = worst with { IoWorkloads = worst.Workloads, IoRest = worst.Rest, AllWorkloads = worst.Workloads };
        vm.Update(worst);
        vm.ShowSpeedResult(new Core.Network.SpeedResult(999, 9999, 9999, 699_000_000,
            new("Wi-Fi", "Intel(R) Wi-Fi 7 BE202 320MHz Wireless Network Adapter", 5764)) { BackgroundDownMbps = 900, BackgroundUpMbps = 900 });
        var flyout = new FlyoutWindow(vm);
        var mini = new MiniWindow(vm, engine, () => { }, () => { });
        if (vm.Scene is { } scene)
        {
            flyout.UpdateScene(scene);
            mini.UpdateScene(scene);
        }
        ImageExport.Layout(flyout, flyout.Width);
        var problems = TextFit.Clipped(flyout).Select(p => "flyout: " + p).ToList();
        ImageExport.Save(flyout, flyout.Width, Path.ChangeExtension(path, ".png"));
        // Every mini layout (not saved to settings: this is a picture).
        foreach (var layout in new[] { "full", "strip", "lighthouse", "numbers" })
        {
            engine.Settings.MiniLayout = layout;
            mini.ApplyLayout();
            ImageExport.Layout(mini, mini.LayoutWidth);
            problems.AddRange(TextFit.Clipped(mini).Select(p => $"mini, {layout}: " + p));
            ImageExport.Save(mini, mini.LayoutWidth, Path.ChangeExtension(path, null) + $"-mini-{layout}.png");
        }
        // The same worst case in the Disk & network view (with its "needs the sensor helper" note).
        vm.ShowView(io: true);
        ImageExport.Layout(flyout, flyout.Width);
        problems.AddRange(TextFit.Clipped(flyout).Select(p => "flyout, disk & network: " + p));
        ImageExport.Save(flyout, flyout.Width, Path.ChangeExtension(path, null) + "-io.png");
        // Explain, which a game turns off: its links on the cards and a long conversation with the follow-up box.
        vm.PreviewLlm = true;
        vm.ShowView(io: false);
        bool NotGame(WorkloadUsage w) => w.Kind != WorkloadKind.Game;
        vm.Update(worst with
        {
            Workloads = [.. worst.Workloads.Where(NotGame)], AllWorkloads = [.. worst.AllWorkloads.Where(NotGame)],
            IoWorkloads = [.. worst.IoWorkloads.Where(NotGame)],
        });
        vm.ShowExplainPreview(
            ($"About \u201c{pipeline}\u201d",
                "It is the data pipeline's transform step, using all 32 cores and 63.9 GB of RAM, which leaves nothing for anything " +
                "else. Memory is nearly full, so Windows is paging. If you need the PC meanwhile, run it with fewer workers."),
            ("Why is the GPU at 110°C and 335 W when no game is running and nothing seems to be using it at all?",
                "The local LLM (Qwen3-30B-A3B-Instruct) is generating on the GPU, which keeps it at full power. 110°C is its " +
                "hotspot limit: the fans are probably not keeping up. Check that the case fans are spinning and the filters are clean."));
        ImageExport.Layout(flyout, flyout.Width);
        problems.AddRange(TextFit.Clipped(flyout).Select(p => "flyout, explain: " + p));
        ImageExport.Save(flyout, flyout.Width, Path.ChangeExtension(path, null) + "-explain.png");
        // The History page: the job runs seeded above (the longest label), with the first row's detail open.
        vm.ShowHistory("Temps", TimeSpan.FromDays(7));
        vm.HistoryTabs.First(t => t.Label == "Jobs").Select.Execute(null);
        vm.HistoryRows.FirstOrDefault()?.Toggle.Execute(null);
        ImageExport.Layout(flyout, flyout.Width);
        problems.AddRange(TextFit.Clipped(flyout).Select(p => "flyout, history: " + p));
        ImageExport.Save(flyout, flyout.Width, Path.ChangeExtension(path, null) + "-history.png");
        vm.ShowMain();
        File.WriteAllText(path, problems.Count == 0 ? "nothing clipped" + Environment.NewLine : string.Join(Environment.NewLine, problems) + Environment.NewLine);
    }

    /// <summary>
    /// `Lighthouse.exe --history-image <history.db> <file>`: the flyout's History page drawn from that history file
    /// (pass a copy: it is opened like the app's own), one picture per list (-events, -games, -jobs, -speed) and one
    /// per chart (-cpu, -temps over 7 days).
    /// </summary>
    public static void HistoryImage(string historyDb, string path)
    {
        Theme.Apply();
        Gauge.Animate = false;
        using var engine = new MonitorEngine(Settings.Load(), diagnostic: true, historyPath: historyDb);
        var vm = new FlyoutViewModel(engine, _ => { });
        var flyout = new FlyoutWindow(vm);
        string stem = Path.ChangeExtension(path, null);
        void Shot(string name)
        {
            ImageExport.Layout(flyout, flyout.Width);
            ImageExport.Save(flyout, flyout.Width, $"{stem}-{name}.png");
        }
        vm.ShowHistory("CPU", TimeSpan.FromHours(24));
        foreach (var (tab, name) in new[] { ("Events", "events"), ("Games", "games"), ("Jobs", "jobs"), ("Speed tests", "speed") })
        {
            vm.HistoryTabs.First(t => t.Label == tab).Select.Execute(null);
            Shot(name);
        }
        vm.HistoryRanges.First(r => r.Label == "7 days").Select.Execute(null);
        vm.HistoryMetrics.First(m => m.Label == "Temps").Select.Execute(null);
        Shot("temps");
    }

    private static void Arrange(FrameworkElement e, double w, double h)
    {
        e.Measure(new Size(w, h));
        e.Arrange(new Rect(0, 0, w, h));
        e.UpdateLayout();
    }

    private static void Save(Visual v, double w, double h, string path)
    {
        var bitmap = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(v);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    /// <summary>Four gauges at the values from a busy minute.</summary>
    private sealed class StackPanelOfGauges
    {
        public FrameworkElement Root { get; }

        public StackPanelOfGauges()
        {
            Gauge.Animate = false;
            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Background = Theme.Brush("Bg"),
            };
            foreach (var (value, brush) in new[] { (0.21, "Accent"), (0.97, "Warn"), (0.32, "Accent"), (0.92, "Hot") })
                panel.Children.Add(new Gauge
                {
                    Width = 72, Height = 72, Margin = new Thickness(13, 4, 13, 4), Value = value, Text = $"{value * 100:0}%",
                    Stroke = Theme.Brush(brush), Track = Theme.Brush("Line"), Foreground = Theme.Brush("Ink"),
                });
            Root = panel;
        }
    }

    /// <summary>The 16 px icon at 8x, on a light and a dark taskbar: calm, busy, hot, and the lamp's blink-off frame.</summary>
    private static void TrayIcons(string path)
    {
        var states = new (double Cpu, Heat CpuHeat, double Gpu, Heat GpuHeat, bool Lit)[]
        {
            (5, Heat.Cool, 3, Heat.Cool, true),
            (45, Heat.Warm, 80, Heat.Cool, true),
            (95, Heat.Hot, 99, Heat.Hot, true),
            (95, Heat.Hot, 99, Heat.Hot, false),
        };
        const int size = 16, zoom = 8, cell = size * zoom + 24;
        using var sheet = new D.Bitmap(cell * states.Length, cell * 2);
        using var g = D.Graphics.FromImage(sheet);
        g.InterpolationMode = D.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = D.Drawing2D.PixelOffsetMode.Half;
        foreach (var (light, row) in new[] { (true, 0), (false, 1) })
        {
            using var taskbar = new D.SolidBrush(light ? D.Color.FromArgb(243, 243, 243) : D.Color.FromArgb(32, 32, 32));
            g.FillRectangle(taskbar, 0, row * cell, sheet.Width, cell);
            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                using var icon = TrayIconRenderer.Render(size, s.Cpu, s.CpuHeat, s.Gpu, s.GpuHeat, light, s.Lit);
                g.DrawImage(icon, i * cell + 12, row * cell + 12, size * zoom, size * zoom);
            }
        }
        sheet.Save(path, D.Imaging.ImageFormat.Png);
    }
}
