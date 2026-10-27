using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lighthouse.Controls;
using Lighthouse.Core;
using Lighthouse.Core.History;
using Lighthouse.Core.Model;
using Lighthouse.Core.Network;
using Lighthouse.Core.Rendering;
using Lighthouse.Core.Workloads;

namespace Lighthouse;

/// <summary>
/// `Lighthouse.exe --readme-images <dir>`: the README's pictures, drawn from made-up demo data (nothing from this
/// PC, its settings or its history): the flyout in both views and both themes, the mini monitor's layouts over a
/// wallpaper, four scenes, and an animated scene.
/// </summary>
internal static class ReadmeImages
{
    private const double Sharp = 192; // twice the size, so the pictures stay crisp on high-DPI screens
    private static readonly DateTime Evening = DateTime.Today.AddHours(20).AddMinutes(10); // the scene's time of day

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        Gauge.Animate = false;
        using var engine = new MonitorEngine(new Settings { InternetPlan = new InternetPlan(500, 50) }, diagnostic: true);
        engine.Machine = engine.Machine with { CpuName = "Ryzen 9 7950X3D", GpuName = "Radeon RX 7900 XTX", RamInstalledGb = 64 };
        const string backup = "Photo backup";
        string label = backup + ": " + JobText.DefaultLabel("python backup.py nightly --to nas");
        var now = DateTime.Now; // the job card counts from the real clock
        foreach (var (daysAgo, minutes) in new[] { (1, 41), (2, 47), (3, 44) })
        {
            var start = now.AddDays(-daysAgo);
            engine.History.AddJobRun(new JobRun(backup, JobText.RunKind(label), label, start, start.AddMinutes(minutes), 0));
        }

        var vm = new FlyoutViewModel(engine, _ => { }) { PreviewLlm = true }; // the demo runs a local LLM
        var flyout = new FlyoutWindow(vm);
        var demo = Demo(label, now);
        foreach (bool light in new[] { true, false })
        {
            Theme.Apply(light);
            string theme = light ? "light" : "dark";
            // The same cards in both views, so the two pictures are the same height side by side.
            vm.ShowSpeedResult(new SpeedResult(14, 486, 47, 690_000_000, new ConnectionInfo("Ethernet", "Realtek PCIe 2.5GbE Family Controller", 2500)));
            vm.ShowView(io: false);
            vm.Update(demo);
            ShowScene(flyout.Scene, vm.Scene, Evening, 2.5);
            Settle();
            ImageExport.Write(ImageExport.Render(flyout, flyout.Width, solidBackground: true, Sharp), Path.Combine(dir, $"flyout-{theme}.png"));
            vm.ShowView(io: true);
            vm.Update(demo);
            Settle();
            ImageExport.Write(ImageExport.Render(flyout, flyout.Width, solidBackground: true, Sharp), Path.Combine(dir, $"flyout-io-{theme}.png"));
            vm.SpeedCloseCommand.Execute(null);
        }

        Theme.Apply(light: true);
        vm.ShowView(io: false);
        vm.Update(demo);
        var mini = new MiniWindow(vm, engine, () => { }, () => { });
        ShowScene(mini.Scene, vm.Scene, Evening, 2.5);
        Settle(); // the gauges are list items, built on a later pass
        MiniLayouts(mini, engine, Path.Combine(dir, "mini-layouts.png"));
        Scenes(Path.Combine(dir, "scenes.png"));
        AnimatedScene(Path.Combine(dir, "scene.png"));
    }

    /// <summary>
    /// A game, a watched backup job, the local LLM and Claude Code on a 16-core PC. As in the app, the rows plus
    /// "Everything else" add up to the CPU, memory, disk and network totals. GPU load is the busiest engine, which
    /// rows can't add up to, so "Everything else" has none ("—").
    /// </summary>
    private static Snapshot Demo(string backupLabel, DateTime now)
    {
        WorkloadUsage W(string name, WorkloadKind kind, double cpu, double gpu, double ram, double vram, double disk = 0,
            double down = 0, double up = 0, DateTime? started = null) =>
            new(name, name, kind, cpu, gpu, ram, vram, 1, started, DiskMBps: disk, NetDownMbps: down, NetUpMbps: up);
        var game = W("Cyberpunk 2077", WorkloadKind.Game, 18.4, 94, 11.2, 9.8, disk: 4.1, down: 2.4, up: 0.8);
        var backup = W(backupLabel, WorkloadKind.Job, 3.2, 0, 0.6, 0, disk: 52, down: 0.1, up: 412, started: now.AddMinutes(-28));
        var llm = W("Local LLM · Qwen3-30B-A3B", WorkloadKind.LocalLlm, 2.1, 0, 1.1, 4.6);
        var claude = W("Claude Code · my-app", WorkloadKind.ClaudeCode, 1.4, 0, 0.9, 0);
        var chrome = W("Chrome", WorkloadKind.Browser, 2.6, 2, 3.4, 0.6, disk: 0.6, down: 3.1, up: 0.4);
        var steam = W("Steam", WorkloadKind.App, 1.9, 0, 0.8, 0, disk: 38, down: 286, up: 1.2);
        // CPU 32%, RAM 23.5 GB, VRAM 15.9 GB; disk 98 MB/s, down 296.1 Mbps, up 417 Mbps (GPU 97% is the game's engine).
        var rest = W("Everything else", WorkloadKind.Windows, 4.3, double.NaN, 6.3, 0.9);
        var ioRest = W("Everything else", WorkloadKind.Windows, 5.9, double.NaN, 7.4, 5.5, disk: 3.3, down: 4.5, up: 2.6);
        return new Snapshot
        {
            Time = now,
            CpuLoad = 32, CpuTemp = 71, CacheCcdTemp = 69, OtherCcdTemp = 58, CpuPower = 96, CpuClockMhz = 5150,
            GpuLoad = 97, GpuTemp = 68, GpuHotspot = 84, GpuPower = 318,
            VramUsedGb = 15.9, VramTotalGb = 24, RamUsedGb = 23.5, RamTotalGb = 64, CommitPct = 41,
            DimmTemp = 46, NvmeTemp = 52, DiskActivePct = 38, FullSensors = true, IoTraced = true,
            NetDownBytesPerSec = 296.1e6 / 8, NetUpBytesPerSec = 417e6 / 8, DiskMBps = 98,
            Workloads = [game, backup, llm, claude, chrome],
            Rest = rest,
            IoWorkloads = [backup, steam, game, chrome],
            IoRest = ioRest,
            AllWorkloads = [game, backup, llm, claude, chrome, steam],
            Self = W("Lighthouse", WorkloadKind.Lighthouse, 0.1, 0, 0.1, 0),
        };
    }

    private static void ShowScene(HarbourScene scene, SceneState? state, DateTime clock, double seconds)
    {
        if (state is null) return;
        scene.Clock = clock;
        scene.Update(state);
        scene.Step(seconds);
        scene.InvalidateVisual();
    }

    /// <summary>The four layouts over a wallpaper that runs from dark to light, each with its text coloured for its spot.</summary>
    private static void MiniLayouts(MiniWindow mini, MonitorEngine engine, string path)
    {
        const double gap = 28;
        string[][] rows = [["full", "strip"], ["lighthouse", "numbers"]];
        var sizes = new Dictionary<string, Size>();
        foreach (string layout in rows.SelectMany(r => r))
        {
            engine.Settings.MiniLayout = layout;
            mini.ApplyLayout();
            Settle();
            ImageExport.Layout(mini, mini.LayoutWidth);
            var panel = (FrameworkElement)mini.Content;
            sizes[layout] = new Size(Math.Ceiling(panel.ActualWidth), Math.Ceiling(panel.ActualHeight));
        }
        double width = rows.Max(r => r.Sum(l => sizes[l].Width) + gap * (r.Length - 1)) + 2 * gap;
        double height = rows.Sum(r => r.Max(l => sizes[l].Height)) + gap * (rows.Length + 1);
        var wallpaper = Draw(width, height, dc =>
        {
            var sky = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(15, 28, 46), 0), new(Color.FromRgb(61, 90, 128), 0.5), new(Color.FromRgb(224, 231, 238), 1),
            }, new Point(0, 0.3), new Point(1, 0.7));
            dc.DrawRectangle(sky, null, new Rect(0, 0, width, height));
            var glow = new RadialGradientBrush(Color.FromArgb(90, 241, 227, 143), Color.FromArgb(0, 241, 227, 143));
            dc.DrawEllipse(glow, null, new Point(width * 0.62, height * 0.2), width * 0.3, height * 0.5);
        });
        var placed = new List<(BitmapSource Image, Rect At)>();
        double y = gap;
        foreach (var row in rows)
        {
            double x = gap;
            foreach (string layout in row)
            {
                engine.Settings.MiniLayout = layout;
                mini.ApplyLayout();
                Settle();
                var at = new Rect(new Point(x, y), sizes[layout]);
                placed.Add((mini.RenderOver((w, h, _, _) => new CroppedBitmap(wallpaper,
                    new Int32Rect((int)(at.X * Sharp / 96), (int)(at.Y * Sharp / 96), w, h)), Sharp), at));
                x += sizes[layout].Width + gap;
            }
            y += row.Max(l => sizes[l].Height) + gap;
        }
        ImageExport.Write(Draw(width, height, dc =>
        {
            dc.DrawImage(wallpaper, new Rect(0, 0, width, height));
            foreach (var (image, at) in placed) dc.DrawImage(image, new Rect(at.X, at.Y, image.Width, image.Height));
        }), path);
    }

    private static readonly (string Caption, int Hour, double Seconds, SceneState State)[] Situations =
    [
        ("Two watched jobs out at sea", 13, 3, new(45, Heat.Warm, 10, 0.45, [new("a", "backup", 8), new("b", "render", 30)], null, false, false)),
        ("A game is the ship, the local LLM the moon", 23, 3, new(60, Heat.Warm, 96, 0.6, [], "Cyberpunk 2077", true, false)),
        ("A warning brings a storm", 15, 3, new(80, Heat.Hot, 40, 0.7, [], null, false, false,
            Weather: new SceneWeather(Storm: true, Lightning: false, Fog: 0))),
        ("A speed test is light crossing the sea", 19, 3, new(6, Heat.Cool, 3, 0.3, [], null, false, false,
            new SceneSignal(SignalPhase.Download, 486, "↓ 486 Mbps"))),
    ];

    /// <summary>
    /// Four situations in a 2 × 2 grid with a caption under each, on a see-through background with captions in a
    /// grey that reads on both GitHub's light and dark pages.
    /// </summary>
    private static void Scenes(string path)
    {
        var caption = new SolidColorBrush(Color.FromRgb(0x76, 0x7F, 0x8A));
        var grid = new UniformGrid { Columns = 2, Background = Brushes.Transparent };
        foreach (var (text, hour, seconds, state) in Situations)
        {
            var scene = new HarbourScene { Height = 150 };
            ShowScene(scene, state, DateTime.Today.AddHours(hour), seconds);
            grid.Children.Add(new StackPanel
            {
                Margin = new Thickness(8),
                Children =
                {
                    scene,
                    new TextBlock
                    {
                        Text = text, Margin = new Thickness(2, 6, 0, 0), FontSize = 12, Foreground = caption,
                        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
                    },
                },
            });
        }
        const double width = 2 * (394 + 16);
        grid.Measure(new Size(width, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
        grid.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * Sharp / 96), (int)Math.Ceiling(grid.ActualHeight * Sharp / 96), Sharp, Sharp, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        ImageExport.Write(bitmap, path);
    }

    /// <summary>
    /// Four seconds of an evening at 12 frames a second: a game, a job, gulls flying in with a download and
    /// foam from the disk. An animated PNG, not a GIF: full colour, so the dusk sky has no bands, and see-through
    /// rounded corners that suit GitHub's light and dark pages alike.
    /// </summary>
    private static void AnimatedScene(string path)
    {
        const double w = 394, h = 150, ppi = 144;
        const int fps = 12, frames = 48;
        var scene = new HarbourScene { Clock = DateTime.Today.AddHours(19).AddMinutes(40) };
        scene.Update(new SceneState(55, Heat.Warm, 70, 0.55, [new("a", "backup", 10)], "Cyberpunk 2077",
            Moon: true, MoonThinking: false, NetDownMbps: 280, DiskActivity: 0.6));
        scene.Step(2);
        var pngs = new List<byte[]>();
        for (int i = 0; i < frames; i++)
        {
            scene.Step(1.0 / fps);
            scene.InvalidateVisual();
            scene.Measure(new Size(w, h));
            scene.Arrange(new Rect(0, 0, w, h));
            scene.UpdateLayout();
            var frame = new RenderTargetBitmap((int)(w * ppi / 96), (int)(h * ppi / 96), ppi, ppi, PixelFormats.Pbgra32);
            frame.Render(scene);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            pngs.Add(buffer.ToArray());
        }
        File.WriteAllBytes(path, Apng.Make(pngs, 1, fps));
    }

    /// <summary>Lets WPF finish what a binding change queued (new list items are built on a later pass).</summary>
    private static void Settle() =>
        Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static RenderTargetBitmap Draw(double width, double height, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) paint(dc);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * Sharp / 96), (int)Math.Ceiling(height * Sharp / 96), Sharp, Sharp, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
