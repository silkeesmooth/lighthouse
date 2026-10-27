using Lighthouse.Core;
using Lighthouse.Core.Autostart;
using Lighthouse.Core.Health;
using Lighthouse.Core.History;
using Lighthouse.Core.Llm;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;
using Lighthouse.Core.Rendering;
using Lighthouse.Core.Sensors;
using Lighthouse.Core.Tracking;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

[Collection("GDI handles")] // counts the process's GDI handles, so nothing else draws meanwhile
public class TrayIconRendererTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Renders_every_tray_size(int size)
    {
        using var bmp = TrayIconRenderer.Render(size, 50, Heat.Warm, 100, Heat.Hot, lightTaskbar: true);
        Assert.Equal(size, bmp.Width);
        // Bottom-left pixel is CPU fill, top-right is GPU fill (100%), and the margin column stays clear.
        var l = TrayIconRenderer.Layout.For(size);
        Assert.Equal(Palette.KhakiDeep.ToArgb(), bmp.GetPixel(l.Margin + 1, size - l.Margin - 1).ToArgb()); // CPU fill
        Assert.Equal(Palette.BittersweetDeep.ToArgb(), bmp.GetPixel(size - l.Margin - 2, l.BarTop).ToArgb()); // GPU full
        Assert.Equal(Palette.BittersweetDeep.ToArgb(), bmp.GetPixel(size / 2, l.Margin + l.Lamp / 2).ToArgb()); // lamp: hotter chip
        Assert.Equal(0, bmp.GetPixel(0, size / 2).A);
    }

    [Fact]
    public void Ten_thousand_redraws_leak_no_handles()
    {
        IntPtr me = System.Diagnostics.Process.GetCurrentProcess().Handle;
        for (int i = 0; i < 50; i++) TrayIconRenderer.Destroy(TrayIconRenderer.RenderHicon(16, i, Heat.Cool, i, Heat.Cool, true));
        Settle(); // handles other tests left to finalizers would otherwise be freed mid-count
        uint gdi = NativeMethods.GetGuiResources(me, 0), user = NativeMethods.GetGuiResources(me, 1);
        for (int i = 0; i < 10_000; i++)
        {
            IntPtr h = TrayIconRenderer.RenderHicon(16, i % 101, (Heat)(i % 3), (i * 7) % 101, (Heat)(i % 3), i % 2 == 0);
            TrayIconRenderer.Destroy(h);
        }
        Settle();
        Assert.InRange((int)NativeMethods.GetGuiResources(me, 0) - (int)gdi, -5, 5);
        Assert.InRange((int)NativeMethods.GetGuiResources(me, 1) - (int)user, -5, 5);
    }

    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Theory]
    [InlineData(0, 14, 0)]
    [InlineData(0.4, 14, 0)]
    [InlineData(2, 14, 1)] // any real load shows at least one row
    [InlineData(50, 14, 7)]
    [InlineData(100, 14, 14)]
    public void Fill_rows(double load, int height, int rows) => Assert.Equal(rows, TrayIconRenderer.FillRows(load, height));

    [Fact]
    public void Heat_bands()
    {
        Assert.Equal(Heat.Cool, TrayIconRenderer.CpuHeat(null));
        Assert.Equal(Heat.Warm, TrayIconRenderer.CpuHeat(75));
        Assert.Equal(Heat.Hot, TrayIconRenderer.CpuHeat(90));
        Assert.Equal(Heat.Hot, TrayIconRenderer.GpuHeat(70, 101)); // a hot hotspot wins over a cool edge
    }
}

public class RulesTests
{
    private static readonly Dictionary<string, string> Meanings = new() { ["4"] = "stopped: spend cap or outage (exit 4)" };

    [Theory]
    [InlineData(0, "finished")]
    [InlineData(4, "stopped: spend cap or outage (exit 4)")]
    [InlineData(-1073741510, "was interrupted (Ctrl+C)")]
    [InlineData(42, "ended with exit 42")]
    public void Exit_codes_use_the_jobs_own_meanings_first(int code, string meaning) =>
        Assert.Equal(meaning, JobText.Describe(code, Meanings));

    [Theory]
    [InlineData("python -m pipeline load --region west --run", "load --region west --run")]
    [InlineData("python -m pipeline.backfill run x.py", "backfill run x.py")]
    [InlineData("python -m pipeline.cli verify --region south", "verify --region south")]
    [InlineData("python -m pipeline", "")]
    [InlineData("python train.py --epochs 3", "train.py --epochs 3")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" scripts\\build.mjs --watch", "build.mjs --watch")]
    [InlineData("cargo run --release", "run --release")]
    [InlineData("node", "")]
    public void Default_job_labels_keep_the_interesting_part(string cmd, string expected) =>
        Assert.Equal(expected, JobText.DefaultLabel(cmd));

    [Fact]
    public void Real_commands_show_whole_and_only_inline_scripts_are_capped()
    {
        const string load = "load --source warehouse --from 2026-01-01 --to 2026-10-01 --dry-run";
        Assert.Equal(load, JobText.DefaultLabel("python -m etl " + load));
        string script = JobText.DefaultLabel("python -c \"" + string.Join("; ", Enumerable.Repeat("import os", 30)) + "\"");
        Assert.Equal(JobText.MaxLabel, script.Length);
        Assert.EndsWith("…", script);
    }

    [Fact]
    public void A_job_rule_that_does_not_compile_is_skipped_not_fatal()
    {
        var m = new JobMatcher([new JobRule { Name = "Broken", Match = "(" }, new JobRule { Name = "Build", Match = @"build\.mjs" }]);
        Assert.Equal("Build: build.mjs", m.Find("node build.mjs", "node.exe")?.Label);
        Assert.Null(m.Find("node other.mjs", "node.exe"));
    }

    [Fact]
    public void The_example_settings_file_parses_and_its_jobs_match()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "docs", "settings.example.json"))) dir = dir.Parent!;
        string json = File.ReadAllText(Path.Combine(dir.FullName, "docs", "settings.example.json"));
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(json, Settings.JsonOptions)!;
        var jobs = settings.Jobs;
        Assert.Equal(["task \\VendorUpdate", "HKLM Run \"Contoso RGB\"", "Contoso.Updater notifications"],
            settings.AutostartOff.Where(e => e.Kind != "cmdpal").Select(e => e.ToString()));
        Assert.Equal("Video encoding", settings.WorkloadNames.Single().Label);
        Assert.Equal(["Nightly import", "Training", "Dev server"], jobs.Select(j => j.Name));
        Assert.Equal("could not reach the source (exit 3)", JobText.Describe(3, jobs[0].ExitCodes));

        var m = new JobMatcher(jobs);
        Assert.Equal("Nightly import: load --since yesterday", m.Find("python -m etl load --since yesterday", "python.exe")?.Label);
        Assert.Equal("Training: train.py --epochs 3", m.Find(@"C:\venv\Scripts\python.exe train.py --epochs 3", "python.exe")?.Label);
        var dev = m.Find("uvicorn app:app --port 8000", "uvicorn.exe");
        Assert.Equal(("Dev server: 8000", false), (dev?.Label, dev?.Rule.Notify));
    }

    [Fact]
    public void Finished_run_message()
    {
        var run = new JobFinished(TestJobs.Pipeline, "Pipeline: load --region west --run", TimeSpan.FromMinutes(134), 0);
        Assert.Equal("Pipeline `load --region west --run` finished after 2h 14m.", run.Message);
        var stopped = new JobFinished(TestJobs.Pipeline, "Pipeline", TimeSpan.FromSeconds(42), 4);
        Assert.Equal("Pipeline stopped: spend cap or outage (exit 4) after 42s.", stopped.Message);
        Assert.False(stopped.Succeeded);
    }

    [Theory]
    [InlineData("Pipeline: load --region west --run", "load west")]
    [InlineData("Pipeline: backfill run x.py", "backfill run")]
    [InlineData("Training: train.py --epochs 3", "train.py 3")]
    [InlineData("Pipeline", "Pipeline")]
    public void Boat_name_tags(string label, string expected) => Assert.Equal(expected, SceneMath.ShortRunLabel(label));

    [Fact]
    public void Llm_server_from_its_command_line()
    {
        var s = LlmServer.Parse(1, @"C:\llm\bin\llama-server.exe -m C:\llm\models\Qwen3-30B-A3B-UD-Q4_K_XL.gguf --host 127.0.0.1 --port 8081 --api-key-file C:\llm\api_key.txt");
        Assert.NotNull(s);
        Assert.Equal("http://127.0.0.1:8081", s.LocalBaseUrl);
        Assert.Equal(@"C:\llm\api_key.txt", s.ApiKeyFile);
        Assert.Equal("Qwen3-30B-A3B", s.ModelShort);
    }

    [Fact]
    public void A_server_under_another_account_restarts_through_runas_in_the_chosen_folder()
    {
        string cmd = @"C:\llm\bin\llama-server.exe -m C:\llm\models\m.gguf --port 8081";
        string start = LocalLlm.StartCommand(cmd, @"PC\llm-runner", @"C:\llm", currentUser: "you");
        Assert.Equal(@"cd 'C:\llm'; runas /user:'llm-runner' 'C:\llm\bin\llama-server.exe -m C:\llm\models\m.gguf --port 8081'", start);
        Assert.Equal("llm-runner", LocalLlm.RunasUser(start));
    }

    [Fact]
    public void A_server_under_your_own_account_restarts_directly_in_its_folder()
    {
        string cmd = "\"C:\\Program Files\\llama\\llama-server.exe\" -m D:\\models\\m.gguf --port 8080";
        string start = LocalLlm.StartCommand(cmd, @"DESKTOP\you", currentUser: "you");
        Assert.Equal(@"Start-Process -FilePath 'C:\Program Files\llama\llama-server.exe' -ArgumentList '-m D:\models\m.gguf --port 8080' " +
                     @"-WorkingDirectory 'C:\Program Files\llama'", start);
        Assert.Null(LocalLlm.RunasUser(start));
    }

    [Fact]
    public void Pasting_the_start_command_runs_nothing_hidden_in_the_command_line()
    {
        // Any program can start a "llama-server.exe" whose command line carries PowerShell for the paste to run.
        string cmd = @"C:\x\llama-server.exe --alias it's'; Start-Process calc; '$(calc)";
        string start = LocalLlm.StartCommand(cmd, null, currentUser: "you");
        Assert.Equal(@"Start-Process -FilePath 'C:\x\llama-server.exe' -ArgumentList '--alias it''s''; Start-Process calc; ''$(calc)' " +
                     @"-WorkingDirectory 'C:\x'", start);
        // Every quote in the arguments is doubled, so the whole tail stays one literal string.
        string tail = start[(start.IndexOf("-ArgumentList '", StringComparison.Ordinal) + 15)..start.IndexOf("' -WorkingDirectory", StringComparison.Ordinal)];
        Assert.DoesNotMatch("(?<!')'(?!')", tail);
    }

    [Theory]
    [InlineData("127.0.0.1", 8081, "http://127.0.0.1:8081")]
    [InlineData("0.0.0.0", 8080, "http://127.0.0.1:8080")]
    [InlineData("::1", 8080, "http://[::1]:8080")]
    [InlineData("192.168.1.20", 8080, null)]
    [InlineData("attacker.example", 80, null)]
    [InlineData("127.0.0.1", 0, null)]
    public void Explain_only_talks_to_a_server_on_this_pc(string host, int port, string? url) =>
        Assert.Equal(url, new LlmServer(1, host, port, null, null, "").LocalBaseUrl);

    [Theory]
    [InlineData("python -m etl load --token abc123 --db=x", "load --token ••• --db=x")]
    [InlineData("node deploy.js --password=hunter2 --env prod", "deploy.js --password=••• --env prod")]
    [InlineData("python sync.py API_KEY=sk-123 --verbose", "sync.py API_KEY=••• --verbose")]
    [InlineData("python -m etl load --auth-file C:\\keys\\a.json", "load --auth-file •••")]
    [InlineData("python -m etl load --tokenize --batch 5", "load --tokenize --batch 5")]
    public void Secrets_never_reach_a_label(string cmd, string label) => Assert.Equal(label, JobText.DefaultLabel(cmd));

    [Fact]
    public void The_explain_prompt_describes_the_machine_it_runs_on()
    {
        var machine = new MachineInfo("Ryzen 9 7950X3D", "RX 7900 XTX", 64, X3dTopology, new("AMD Radeon RX 7900 XTX", 24, 0, 0));
        Assert.Equal("Ryzen 9 7950X3D (2 CCDs, one with 3D V-Cache), RX 7900 XTX 24 GB, 64 GB RAM", machine.Describe());
        Assert.Contains("(Ryzen 9 7950X3D (2 CCDs", LocalLlm.SystemPrompt(machine.Describe()));
        var plain = new MachineInfo("Core i7-14700K", "RTX 4070", 32, new CpuTopology([]), null);
        Assert.Equal("Core i7-14700K, RTX 4070, 32 GB RAM", plain.Describe());
    }

    private static readonly CpuTopology X3dTopology = CpuTopology.FromL3([(96L << 20, 0x0000FFFFUL, 0), (32L << 20, 0xFFFF0000UL, 0)]);

    [Fact]
    public void Thinking_is_stripped() => Assert.Equal("Calm.", LocalLlm.StripThinking("<think>hmm\nok</think>\nCalm."));
}

public class HealthTests
{
    [Theory]
    [InlineData("CMH64GX5M2M6000Z30", 6000)]
    [InlineData("F5-6000J3038F16GX2", 6000)]
    [InlineData("KF560C30BBEAK2-32", 6000)] // Kingston: KF5 = DDR5, 60 = 6000
    [InlineData("KF556C40BBK2-32", 5600)]
    [InlineData("M425R1GB4BB0-CQKOL", null)]
    public void Rated_speed_from_part_number(string part, int? speed) => Assert.Equal(speed, HealthChecks.RatedSpeed(part));

    [Fact]
    public void Expo_off_is_reported_with_both_speeds()
    {
        var r = HealthChecks.RamSpeedResult([("CMH64GX5M2M6000Z30", 4800), ("CMH64GX5M2M6000Z30", 4800)]);
        Assert.NotNull(r);
        Assert.False(r.Ok);
        Assert.Equal("RAM runs at 4800 MT/s; the kit is rated 6000 MT/s. Turning on EXPO (AMD) or XMP (Intel) in the BIOS runs it at its rated speed.", r.Detail);
    }

    [Fact]
    public void Expo_on_passes() => Assert.True(HealthChecks.RamSpeedResult([("CMH64GX5M2M6000Z30", 6000)])!.Ok);

    [Fact]
    public void Gpu_idle_power_needs_a_minute_of_idle_samples()
    {
        var few = Enumerable.Range(0, 30).Select(_ => new Snapshot { GpuLoad = 0, GpuPower = 78 }).ToList();
        Assert.Null(HealthChecks.GpuIdlePowerResult(few, monitorsOnGpu: 2));
    }

    [Fact]
    public void Gpu_idle_power_points_at_the_second_monitor()
    {
        // As measured 2026-10-22: 71 W at 2487 MHz with two monitors on the card, whatever the refresh rate.
        var idle = Enumerable.Range(0, 120).Select(i => new Snapshot { GpuLoad = 0.2, GpuPower = i == 5 ? 200 : 71, GpuMemClockMhz = 2487 }).ToList();
        var r = HealthChecks.GpuIdlePowerResult(idle, monitorsOnGpu: 2);
        Assert.NotNull(r);
        Assert.False(r.Ok);
        Assert.Equal("The GPU draws 71 W while idle with its memory clock at 2487 MHz. With 2 monitors on the graphics card its " +
                     "memory can't slow down between frames; plugging one into the motherboard's video output usually fixes it.", r.Detail);
    }

    [Fact]
    public void Gpu_idle_power_with_one_monitor_makes_no_claim_about_why()
    {
        var idle = Enumerable.Range(0, 120).Select(_ => new Snapshot { GpuLoad = 0.2, GpuPower = 71, GpuMemClockMhz = 2487 }).ToList();
        Assert.Equal("The GPU draws 71 W while idle with its memory clock at 2487 MHz.", HealthChecks.GpuIdlePowerResult(idle, 1)!.Detail);
    }

    [Fact]
    public void Gpu_idle_power_ignores_busy_samples()
    {
        var mixed = Enumerable.Range(0, 200).Select(i => i % 2 == 0
            ? new Snapshot { GpuLoad = 95, GpuPower = 300 }
            : new Snapshot { GpuLoad = 1, GpuPower = 22 }).ToList();
        Assert.True(HealthChecks.GpuIdlePowerResult(mixed, 1)!.Ok);
    }
}

public class AutostartGuardTests : IDisposable
{
    private readonly string _marker = Path.Combine(Path.GetTempPath(), $"lighthouse-guard-off-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_marker);

    private sealed class FakeBackend(Dictionary<string, bool?> state) : IAutostartBackend
    {
        public List<string> Disabled { get; } = [];
        public bool? IsEnabled(AutostartEntry e) => state.GetValueOrDefault(e.Name);
        public void Disable(AutostartEntry e)
        {
            Disabled.Add(e.Name);
            state[e.Name] = false;
        }
    }

    private static readonly List<AutostartEntry> Listed =
    [
        new("run", "Contoso RGB", Hive: "HKLM"),
        new("run", "StartAUEP", Hive: "HKLM"),
        new("task", "VendorUpdate", Path: "\\"),
        new("task", "StartDVR", Path: "\\"),
        new("toast", "Contoso.Updater"),
    ];

    [Theory]
    [InlineData("task", @"\", "VendorUpdate", null, true)]
    [InlineData("task", @"\Microsoft\Windows\Windows Defender\", "Windows Defender Scheduled Scan", null, false)]
    [InlineData("task", @"\Microsoft\Windows\WindowsUpdate\", "Scheduled Start", null, false)]
    [InlineData("run", null, "SecurityHealth", "HKLM", false)]
    [InlineData("run", null, "Contoso RGB", "HKLM", true)]
    [InlineData("toast", null, "Windows.SystemToast.SecurityAndMaintenance", null, false)]
    [InlineData("toast", null, "Contoso.Updater", null, true)]
    [InlineData("service", null, "WinDefend", null, false)]
    public void Windows_own_entries_are_never_switched_off(string kind, string? path, string name, string? hive, bool allowed) =>
        Assert.Equal(allowed, AutostartList.Allowed(new AutostartEntry(kind, name, hive, path)));

    [Fact]
    public void The_saved_list_drops_what_it_may_not_touch_and_survives_garbage()
    {
        var list = AutostartList.Parse("""[{"kind":"task","path":"\\","name":"VendorUpdate"},{"kind":"task","path":"\\Microsoft\\Windows\\Defrag\\","name":"ScheduledDefrag"}]""");
        Assert.Equal(["VendorUpdate"], list.Select(e => e.Name));
        Assert.Empty(AutostartList.Parse("not json"));
    }

    [Fact]
    public void With_nothing_listed_the_guard_touches_nothing()
    {
        var backend = new FakeBackend(new() { ["VendorUpdate"] = true, ["Contoso RGB"] = true });
        Assert.Empty(new AutostartGuard(_marker, backend).Enforce([]));
        Assert.Empty(backend.Disabled);
    }

    [Fact]
    public void Only_entries_that_are_on_are_switched_off()
    {
        var backend = new FakeBackend(new()
        {
            ["Contoso RGB"] = false,
            ["StartAUEP"] = false,
            ["VendorUpdate"] = true, // an update put it back
            ["StartDVR"] = null, // gone entirely
            ["Contoso.Updater"] = true,
            ["L-Connect 3"] = true, // not on the list: never touched
        });
        var turnedOff = new AutostartGuard(_marker, backend).Enforce(Listed);
        Assert.Equal(["VendorUpdate", "Contoso.Updater"], backend.Disabled);
        Assert.Equal([@"task \VendorUpdate", "Contoso.Updater notifications"], turnedOff.Select(e => e.ToString()));
    }

    [Fact]
    public void The_off_marker_stops_the_guard()
    {
        File.WriteAllText(_marker, "");
        var backend = new FakeBackend(new() { ["VendorUpdate"] = true });
        Assert.Empty(new AutostartGuard(_marker, backend).Enforce(Listed));
        Assert.Empty(backend.Disabled);
    }

    [Fact]
    public void Dry_run_lists_what_would_change()
    {
        var needs = AutostartGuard.NeedsDisabling(Listed, e => e.Name is "VendorUpdate" or "Contoso RGB");
        Assert.Equal(["Contoso RGB", "VendorUpdate"], needs.Select(e => e.Name));
    }
}

public class TopologyAndGameTests
{
    // A dual-CCD X3D Ryzen as Windows reports it: CCD0 has 96 MB of L3, CCD1 32 MB.
    private static readonly CpuTopology X3d = CpuTopology.FromL3([(96L << 20, 0x0000FFFFUL, 0), (32L << 20, 0xFFFF0000UL, 0)]);

    [Fact]
    public void The_bigger_cache_is_the_v_cache_ccd()
    {
        Assert.Equal(0, X3d.CacheCcd!.Index);
        Assert.Equal(Enumerable.Range(0, 16), X3d.CacheCcd.Processors);
        Assert.Equal(16, X3d.OtherCcd!.Processors[0]);
    }

    [Fact]
    public void Ccd_state_from_parking()
    {
        var parkedOther = Enumerable.Range(0, 32).ToDictionary(i => i, i => i >= 16);
        var none = Enumerable.Range(0, 32).ToDictionary(i => i, _ => false);
        Assert.Equal(CcdState.OtherParked, X3d.StateFrom(parkedOther));
        Assert.Equal(CcdState.BothActive, X3d.StateFrom(none));
        Assert.Equal(CcdState.Unknown, CpuTopology.FromL3([(32L << 20, 0xFFFFUL, 0), (32L << 20, 0xFFFF0000UL, 0)]).StateFrom(none));
    }

    [Fact]
    public void A_game_session_is_summarised_when_the_game_closes()
    {
        var tracker = new GameTracker();
        var game = new WorkloadUsage("game:battlefield 6", "Battlefield 6", WorkloadKind.Game, 20, 90, 9, 8, 4);
        var start = new DateTime(2026, 10, 21, 20, 0, 0);
        GameSession? session = null;
        for (int i = 0; i < 600; i++)
            session ??= tracker.Update(new Snapshot
            {
                Time = start.AddSeconds(i), GpuLoad = 90, GpuHotspot = 80 + i % 9, CpuTemp = 70, VramUsedGb = 17.2,
                Ccd = i % 10 == 0 ? CcdState.BothActive : CcdState.OtherParked, AllWorkloads = [game],
            });
        Assert.Null(session);
        for (int i = 0; i < 10; i++) session ??= tracker.Update(new Snapshot { Time = start.AddSeconds(600 + i) });
        Assert.NotNull(session);
        Assert.Equal("Battlefield 6", session.Game);
        Assert.Equal("GPU avg 90%, hotspot max 88°C, CPU max 70°C, CCD1 parked 90% of the time, VRAM peak 17.2 GB.", session.Summary());
    }
}

public class HistoryTests
{
    [Fact]
    public void Minutes_events_and_games_round_trip()
    {
        string db = Path.Combine(Path.GetTempPath(), $"lighthouse-{Guid.NewGuid():N}.db");
        using (var store = new HistoryStore(db))
        {
            var agg = new MinuteAggregator();
            var t0 = new DateTime(2026, 10, 21, 21, 0, 0);
            MinuteRow? row = null;
            for (int i = 0; i <= 60; i++)
                row ??= agg.Add(new Snapshot { Time = t0.AddSeconds(i), CpuLoad = i < 30 ? 10 : 30, CpuTemp = 50 + i % 7 });
            Assert.NotNull(row);
            Assert.Equal(20, row.Cpu, 6);
            Assert.Equal(56, row.CpuTemp);
            store.AddMinute(row);
            store.AddEvent("job", "Pipeline launch", "finished after 2h 14m.");
            store.AddGame(new GameSession("Battlefield 6", t0, t0.AddHours(1), 90, 74, 88, 70, 95, 17.2));
            Assert.Single(store.Minutes(t0.AddMinutes(-1)));
            Assert.Equal("Pipeline launch", store.Events(10).Single().Title);
            Assert.Equal("Battlefield 6", store.Games(10).Single().Game);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(db);
    }
    [Fact]
    public void A_corrupt_database_is_set_aside_and_a_fresh_one_used()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lighthouse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "history.db");
        File.WriteAllBytes(db, [.. "SQLite format 3\0"u8, .. new byte[4000]]); // a header and nothing valid after it
        using (var store = new HistoryStore(db))
        {
            store.AddEvent("test", "still works", "");
            Assert.Single(store.Events(5));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Single(Directory.GetFiles(dir, "history.corrupt-*.db"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void In_memory_history_writes_no_file()
    {
        using var store = new HistoryStore(HistoryStore.InMemory);
        store.AddEvent("test", "x", "");
        Assert.Single(store.Events(5));
        Assert.False(File.Exists(HistoryStore.InMemory));
    }
}

public class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lighthouse-links-" + Guid.NewGuid().ToString("N"));

    public DataFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in new DirectoryInfo(_root).GetDirectories()) dir.Delete(); // removes junctions, not their targets
        Directory.Delete(_root, true);
    }

    [Fact]
    public void A_junction_is_a_link_and_a_real_folder_is_not()
    {
        string real = Path.Combine(_root, "real"), junction = Path.Combine(_root, "junction");
        Directory.CreateDirectory(real);
        // Any user can make a junction (no admin, no developer mode): the reason the elevated copy checks.
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{real}\"")
               { CreateNoWindow = true, UseShellExecute = false }))
            p!.WaitForExit();
        Assert.True(Paths.IsLink(junction));
        Assert.False(Paths.IsLink(real));
        Assert.False(Paths.IsLink(Path.Combine(_root, "nothing-here")));
    }
}
