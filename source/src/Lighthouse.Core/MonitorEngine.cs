using System.Diagnostics;
using System.Runtime.InteropServices;
using Lighthouse.Core.Alerts;
using Lighthouse.Core.Autostart;
using Lighthouse.Core.Health;
using Lighthouse.Core.History;
using Lighthouse.Core.Llm;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;
using Lighthouse.Core.Network;
using Lighthouse.Core.Sensors;
using Lighthouse.Core.Tracking;
using Lighthouse.Core.Workloads;
using Microsoft.Win32;

namespace Lighthouse.Core;

public sealed record MachineInfo(string CpuName, string GpuName, double RamInstalledGb, CpuTopology Topology, GpuAdapter? Gpu)
{
    public string Line => $"{CpuName} · {GpuName} · {RamInstalledGb:0} GB";

    /// <summary>The machine in a phrase, for the local LLM: "Ryzen 9 7950X3D (two CCDs, one with 3D V-Cache), ...".</summary>
    public string Describe()
    {
        string ccds = Topology.CacheCcd is not null ? $" ({Topology.Ccds.Count} CCDs, one with 3D V-Cache)"
            : Topology.Ccds.Count > 1 ? $" ({Topology.Ccds.Count} CCDs)" : "";
        return $"{CpuName}{ccds}, {GpuName}{(Gpu?.VramGb is > 0 and var v ? $" {v:0} GB" : "")}, {RamInstalledGb:0} GB RAM";
    }
}

/// <summary>
/// The sampling loop: once a second it reads every sensor, names the workloads, builds a <see cref="Snapshot"/>,
/// runs the alert rules and trackers, and records history. The UI only listens.
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private const int RecentCapacity = 600;
    private const double Gb = 1024.0 * 1024 * 1024;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _gate = new();
    private readonly object _settingsGate = new();
    private readonly LinkedList<Snapshot> _recent = new();

    private CounterSampler? _counters;
    private IHardwareSource? _hardware;
    private ProcessMonitor? _processes;
    private ProcessSample? _procSample;
    private AlertEngine _alerts;
    private readonly GameTracker _games = new();
    private readonly JobTracker _jobs = new();
    private readonly MinuteAggregator _minutes = new();
    private readonly AutostartGuard _guard = new(Paths.GuardOffFile, new WindowsAutostartBackend());
    private HardwareReading? _lastHardware;
    private DateTime _nextHealth = DateTime.Now.AddMinutes(2); // the idle-power check needs a minute of samples
    private DateTime _nextGuard = DateTime.Now.AddSeconds(30);
    private DateTime _nextPrune = DateTime.Now.AddMinutes(5);
    private DateTime _speedTestEnded = DateTime.MinValue;

    private readonly bool _diagnostic;

    /// <param name="diagnostic">A --snapshot run: in-memory history, no autostart guard, no health checks or settings writes.</param>
    /// <param name="historyPath">A diagnostic run's history file instead of memory (a copy, for pictures).</param>
    public MonitorEngine(Settings settings, bool diagnostic = false, string? historyPath = null)
    {
        Settings = settings;
        _diagnostic = diagnostic;
        _alerts = new AlertEngine(settings.Thresholds);
        History = new HistoryStore(diagnostic ? historyPath ?? HistoryStore.InMemory : Paths.HistoryDb);
        Machine = new MachineInfo(CpuName(), "GPU", InstalledRamGb(), CpuTopology.Read(), null);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lighthouse sampler", Priority = ThreadPriority.AboveNormal };
    }

    public Settings Settings { get; private set; }
    public HistoryStore History { get; }
    /// <summary>This PC's CPU, GPU and RAM (the README pictures set a stand-in).</summary>
    public MachineInfo Machine { get; set; }
    public Snapshot? Latest { get; private set; }
    public IReadOnlyList<Notice> ActiveAlerts { get; private set; } = [];
    public IReadOnlyList<HealthResult> Health { get; private set; } = [];
    public LlmServer? Llm { get; private set; }
    public string? CurrentGame => _games.CurrentGame;
    public DateTime? GameStarted => _games.Started;
    public bool BothCcdsActiveWarning => _games.BothCcdsActiveWarning;
    public string? HardwareError => _hardware?.Error;

    /// <summary>Raised on the sampler thread after every tick.</summary>
    public event Action<Snapshot>? Updated;

    /// <summary>Raised on the sampler thread for anything worth a toast.</summary>
    public event Action<Notice>? Notify;

    public void Start() => _thread.Start();

    public void ReloadSettings()
    {
        var fresh = Settings.Load();
        lock (_gate)
        {
            // A new engine forgets cooldowns, so only rebuild it when the thresholds really changed.
            if (fresh.Thresholds != Settings.Thresholds) _alerts = new AlertEngine(fresh.Thresholds);
            if (_processes is not null && Settings.Json(fresh.Jobs) + Settings.Json(fresh.WorkloadNames)
                != Settings.Json(Settings.Jobs) + Settings.Json(Settings.WorkloadNames))
                _processes.Classifier = NewClassifier(fresh);
            Settings = fresh;
        }
    }

    public IReadOnlyList<Snapshot> Recent(TimeSpan window)
    {
        lock (_gate)
        {
            if (_recent.Count == 0) return [];
            DateTime from = _recent.Last!.Value.Time - window;
            return _recent.Where(s => s.Time >= from).ToList();
        }
    }

    private void Loop()
    {
        try
        {
            var gpu = Dxgi.MainAdapter();
            _counters = new CounterSampler(gpu);
            // The sensors that need admin come from the sensor helper; only an app that is admin itself reads them here.
            _hardware = UserContext.Elevated ? new LocalHardware(gpu?.Name) : new SensorClient(gpu?.Name);
            _processes = new ProcessMonitor(NewClassifier(Settings), _hardware is SensorClient client ? client.Details : null);
            if (Settings.Jobs.Count > 0) Log.Write($"watching jobs: {string.Join(", ", Settings.Jobs.Select(j => j.Name))}");
            Machine = Machine with { GpuName = ShortGpuName(gpu?.Name), Gpu = gpu };
            if (_hardware is LocalHardware && _hardware.Error is { } error) Log.Write($"hardware sensors unavailable: {error}");
            Log.Write($"started: admin={UserContext.Elevated} sensors={(_hardware is SensorClient ? "helper" : "this process")} gpu={gpu?.Name} luid={gpu?.PdhLuid} " +
                      $"ccds={string.Join("/", Machine.Topology.Ccds.Select(c => $"{c.L3Bytes >> 20}MB"))}");
        }
        catch (Exception e)
        {
            Log.Write($"init failed: {e}");
        }

        var clock = Stopwatch.StartNew();
        long tick = 0;
        while (!_stop.IsSet)
        {
            try
            {
                Tick(tick);
            }
            catch (Exception e)
            {
                Log.Write($"tick {tick} failed: {e}");
            }
            tick++;
            long due = tick * 1000;
            long wait = due - clock.ElapsedMilliseconds;
            if (wait < -3000) // fell far behind (sleep, debugger): start the cadence again
            {
                clock.Restart();
                tick = 0;
                wait = 1000;
            }
            _stop.Wait(TimeSpan.FromMilliseconds(Math.Max(50, wait)));
        }
    }

    private void Tick(long tick)
    {
        if (_counters is null || _hardware is null || _processes is null) return;
        DateTime now = DateTime.Now;
        var counters = _counters.Sample();
        var hw = _hardware.Sample();
        if (_hardware is SensorClient helper)
        {
            foreach (string line in helper.DrainLog()) Log.Write("sensor helper: " + line);
            foreach (string notice in helper.DrainNotices())
                Raise(new Notice("autostart", Severity.Info, "Switched a startup app off", notice), "autostart");
        }
        _lastHardware = hw;

        bool processTick = tick % 2 == 0 || _procSample is null;
        if (processTick)
        {
            var traced = _hardware is SensorClient { IoError: null } client ? client.Io : null;
            _procSample = _processes.Sample(counters.GpuByPid, traced);
            Llm = _procSample.Llm;
        }

        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);
        double ramTotal = mem.ullTotalPhys / Gb, ramUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / Gb;
        double vramUsed = counters.VramUsedBytes / Gb;
        var table = Aggregator.Aggregate(_procSample!.Usage, counters.CpuUtility, ramUsed, vramUsed,
            io: new IoTotals(_procSample.Io.Traced, _procSample.Io.DiskTotalBps, counters.NetDownBytesPerSec, counters.NetUpBytesPerSec));

        var topo = Machine.Topology;
        double? cacheTemp = null, otherTemp = null;
        if (topo.CacheCcd is { } cache && hw.CcdTemps.Count == topo.Ccds.Count)
        {
            cacheTemp = hw.CcdTemps[cache.Index];
            otherTemp = hw.CcdTemps[topo.OtherCcd!.Index];
        }

        var snapshot = new Snapshot
        {
            Time = now,
            CpuLoad = counters.CpuUtility,
            CpuTemp = hw.CpuTemp,
            CacheCcdTemp = cacheTemp,
            OtherCcdTemp = otherTemp,
            CpuPower = hw.CpuPower,
            CpuClockMhz = hw.CpuClockMhz,
            GpuLoad = counters.GpuLoad,
            GpuTemp = hw.GpuTemp,
            GpuHotspot = hw.GpuHotspot,
            GpuMemTemp = hw.GpuMemTemp,
            GpuPower = hw.GpuPower,
            GpuFanRpm = hw.GpuFanRpm,
            GpuMemClockMhz = hw.GpuMemClockMhz,
            VramUsedGb = vramUsed,
            VramTotalGb = Machine.Gpu?.VramGb ?? 0,
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            CommitPct = counters.CommitPct,
            DimmTemp = hw.DimmTemp,
            NvmeTemp = hw.NvmeTemp,
            NvmeWearPct = hw.NvmeWearPct,
            DiskActivePct = counters.DiskActivePct,
            NetDownBytesPerSec = counters.NetDownBytesPerSec,
            NetUpBytesPerSec = counters.NetUpBytesPerSec,
            Ccd = topo.StateFrom(counters.Parked),
            FullSensors = _hardware.Full,
            Workloads = table.Top,
            Rest = table.Rest,
            IoWorkloads = table.TopIo,
            IoRest = table.RestIo,
            DiskMBps = _procSample.Io.DiskTotalBps / 1e6,
            IoTraced = _procSample.Io.Traced,
            AllWorkloads = table.All,
            Self = table.Self,
        };

        Settings settings;
        AlertEngine alerts;
        lock (_gate)
        {
            settings = Settings;
            alerts = _alerts;
            Latest = snapshot;
            // The top rows by load and by disk and network: enough to name who was busy, without every process each second.
            _recent.AddLast(snapshot with { AllWorkloads = table.Top.Concat(table.TopIo).DistinctBy(w => w.Key).ToList() });
            while (_recent.Count > RecentCapacity) _recent.RemoveFirst();
        }

        bool paused = settings.AlertsPausedUntil is { } until && until > now;
        var ctx = new AlertContext(snapshot.Game is not null && settings.QuietInGames, paused, UserIdle());
        foreach (var notice in alerts.Evaluate(snapshot, ctx)) Raise(notice, "alert");
        ActiveAlerts = alerts.Active;

        if (_games.Update(snapshot) is { } session)
        {
            History.AddGame(session);
            string body = session.Summary();
            if (settings.PausedLlmStartCommand is not null) body += " The local LLM is still stopped: open Lighthouse to start it.";
            History.AddEvent("game", $"{session.Game} · {Durations.Format(session.Duration)}", body);
            if (session.Duration.TotalMinutes >= settings.GameSummaryMinMinutes)
                Notify?.Invoke(new Notice("game-summary", Severity.Info, $"{session.Game} · {Durations.Format(session.Duration)}", body));
        }

        if (processTick)
        {
            foreach (var run in _jobs.Update(_procSample.JobRuns))
            {
                string kind = JobText.RunKind(run.Label);
                var usual = UsualDuration(run.Rule.Name, kind);
                History.AddJobRun(new JobRun(run.Rule.Name, kind, run.Label, now - run.Duration, now, run.ExitCode));
                lock (_usual) _usual.Remove((run.Rule.Name, kind));
                string message = run.Message + (usual is { } u && run.Succeeded && run.Duration > u * 1.5
                    ? $" Slower than usual (usually {Durations.Rough(u)})." : "");
                History.AddEvent("job", run.Label, message);
                if (run.Duration.TotalSeconds >= settings.JobToastMinSeconds)
                    Notify?.Invoke(new Notice("job", run.Succeeded ? Severity.Info : Severity.Warning,
                        run.Succeeded ? $"{run.Rule.Name} finished" : $"{run.Rule.Name} stopped", message));
            }
        }

        if (_minutes.Add(snapshot) is { } minute) History.AddMinute(minute);
        Updated?.Invoke(snapshot);
        if (_diagnostic) return;

        // The sensor helper keeps the startup list off; only an app that is admin itself (no helper) does it here.
        if (now >= _nextGuard && _hardware is LocalHardware && UserContext.Elevated)
        {
            _nextGuard = now.AddMinutes(10);
            // The list an administrator saved, never settings.json's: any program can edit that, and this runs as admin.
            foreach (var entry in _guard.Enforce(AutostartList.Read()))
                Raise(new Notice("autostart", Severity.Info, "Switched a startup app off",
                    $"{entry} was on; Lighthouse switched it off again."), "autostart");
        }

        if (now >= _nextHealth)
        {
            _nextHealth = now.AddHours(24);
            RunHealth(settings);
        }

        if (now >= _nextPrune)
        {
            _nextPrune = now.AddDays(1);
            History.Prune();
        }

        if (now >= _nextHourly)
        {
            _nextHourly = now.AddMinutes(10);
            MaybeDigest(settings, now);
        }
        MaybeDailySpeedTest(settings, now);
    }

    private static Classifier NewClassifier(Settings settings) =>
        new(Environment.ProcessId, settings.WorkloadNames, settings.Jobs);

    private void RunHealth(Settings settings)
    {
        var since = settings.LastDriverResetCheck ?? DateTime.Now.AddDays(-7);
        var results = HealthChecks.RunAll(_lastHardware, since, Recent(TimeSpan.FromMinutes(5)), Machine.Gpu?.Name);
        try
        {
            if (Insights.CoolingTrend(History.Minutes(DateTime.Now.AddDays(-60)), DateTime.Now) is { } cooling) results.Add(cooling);
        }
        catch (Exception e)
        {
            Log.Write($"cooling check failed: {e.Message}");
        }
        Health = results;
        var newlyFailing = new List<HealthResult>();
        lock (_settingsGate)
        {
            foreach (var r in results)
            {
                bool wasOk = !settings.LastHealth.TryGetValue(r.Id, out bool ok) || ok;
                if (!r.Ok && wasOk && !settings.DismissedHealth.Contains(r.Id)) newlyFailing.Add(r);
                settings.LastHealth[r.Id] = r.Ok;
            }
            settings.LastDriverResetCheck = DateTime.Now;
        }
        SaveSettings(settings);
        foreach (var r in newlyFailing) Raise(new Notice("health:" + r.Id, Severity.Info, r.Title, r.Detail), "health");
    }

    private void Raise(Notice notice, string kind)
    {
        History.AddEvent(kind, notice.Title, notice.Message);
        Notify?.Invoke(notice);
    }

    public void SaveSettings(Settings? settings = null)
    {
        try
        {
            lock (_settingsGate) (settings ?? Settings).Save();
        }
        catch (Exception e)
        {
            Log.Write($"settings save failed: {e.Message}");
        }
    }

    /// <summary>Hides a health check for good: no card, no toast. Undo by removing it from dismissedHealth in settings.json.</summary>
    public void DismissHealth(string id)
    {
        if (Settings.DismissedHealth.Contains(id)) return;
        Settings.DismissedHealth.Add(id);
        SaveSettings();
        History.AddEvent("health", "Check dismissed", id);
    }

    public void PauseAlerts(TimeSpan span)
    {
        Settings.AlertsPausedUntil = DateTime.Now + span;
        SaveSettings();
    }

    /// <summary>Stops a llama-server (for a game) and keeps the line that starts it again. Never Ollama or LM Studio.</summary>
    public string? StopLlm()
    {
        if (Llm is not { Kind: LlmKind.LlamaServer } server) return null;
        string start = LocalLlm.StartCommand(server.CommandLine, server.Owner ?? ProcessTable.Owner(server.Pid), Settings.LlmStartDirectory);
        // Another account's server (the usual setup) is stopped by the helper, which stops nothing but llama-server.exe.
        bool stopped = _hardware is SensorClient { Connected: true } sensors
            ? sensors.StopLlm(new ProcessKey(server.Pid, server.CreateTime))
            : LocalLlm.Kill(server.Pid);
        if (!stopped) return null;
        Settings.PausedLlmStartCommand = start;
        SaveSettings();
        History.AddEvent("llm", "Local LLM stopped", "Stopped from Lighthouse to free VRAM.");
        return start;
    }

    public void ForgetPausedLlm()
    {
        Settings.PausedLlmStartCommand = null;
        SaveSettings();
    }

    /// <summary>The readings Explain starts a conversation with: the last five minutes and the active alerts.</summary>
    public string ExplainDigest() => LocalLlm.Digest(Recent(TimeSpan.FromMinutes(5)), ActiveAlerts.Select(a => a.Message));

    /// <summary>Asks the local LLM the conversation so far. Not during a game: it would take GPU time from the game.</summary>
    public async Task<LlmAnswer> AskLlmAsync(IReadOnlyList<ChatTurn> conversation, CancellationToken ct)
    {
        if (Llm is not { } server) return new("No local LLM is running, so there is nobody to ask.", false);
        if (CurrentGame is { } game) return new($"Not while {game} is running: the LLM would take GPU time from the game.", false);
        if (server.Kind == LlmKind.LmStudio && Settings.LmStudioPort is { } port) server = server with { Port = port };
        try
        {
            return await LocalLlm.AskAsync(server, Machine.Describe(), conversation, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(server.Kind == LlmKind.LmStudio
                ? $"LM Studio did not answer on port {server.Port}. Start its local server in LM Studio first."
                : $"{server.AppName} did not answer ({e.GetType().Name}).", false);
        }
    }

    /// <summary>Unloads Ollama's models for a game, which frees their VRAM. Ollama loads one again when next asked.</summary>
    public async Task<int> UnloadOllamaAsync(CancellationToken ct)
    {
        if (Llm is not { Kind: LlmKind.Ollama, LocalBaseUrl: { } url }) return 0;
        int unloaded = await LocalLlm.UnloadOllamaAsync(url, ct);
        if (unloaded > 0) History.AddEvent("llm", "Ollama's models unloaded", "Unloaded from Lighthouse to free VRAM for a game.");
        return unloaded;
    }

    /// <summary>
    /// An internet speed test, only when asked. Not while a game runs: it would take the game's bandwidth. Each result
    /// goes into History, so past tests can be compared.
    /// </summary>
    public async Task<SpeedOutcome> SpeedTestAsync(IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        if (CurrentGame is { } game) return new(null, $"Not while {game} is running: the test would take bandwidth from the game.");
        if (Interlocked.Exchange(ref _speedTestRunning, 1) == 1) return new(null, "A speed test is already running.");
        try
        {
            // What was already on the wire: the test can only measure what other traffic leaves it. Not the seconds
            // of a test that just ended: run back to back, its own upload read as "something else uploading 16 Mbps".
            var before = Recent(TimeSpan.FromSeconds(5)).Where(s => s.Time > _speedTestEnded.AddSeconds(2)).ToList();
            bool known = before.Count >= 2;
            SpeedResult result;
            try
            {
                double? bgDown = known ? before.Average(s => s.NetDownBytesPerSec) * 8 / 1e6 : null;
                double? bgUp = known ? before.Average(s => s.NetUpBytesPerSec) * 8 / 1e6 : null;
                result = await SpeedTest.RunAsync(progress, ct) with
                {
                    BackgroundDownMbps = bgDown,
                    BackgroundUpMbps = bgUp,
                    BackgroundBy = BusiestOnTheLine(before.LastOrDefault(), (bgDown ?? 0) + (bgUp ?? 0)),
                };
            }
            finally
            {
                _speedTestEnded = DateTime.Now;
            }
            History.AddEvent("network", "Speed test: " + result.Summary, result.Detail);
            History.AddSpeedTest(new SpeedRun(DateTime.Now, result.DownMbps, result.UpMbps, result.PingMs, result.Connection?.Kind,
                result.BackgroundDownMbps is { } d ? d + (result.BackgroundUpMbps ?? 0) : null));
            Log.Write($"speed test: {result.Summary} {result.Detail}");
            return new(result, null);
        }
        catch (OperationCanceledException)
        {
            return new(null, "The speed test stopped before it finished.");
        }
        catch (HttpRequestException e)
        {
            Log.Write($"speed test failed: {e.Message}");
            return new(null, $"The speed test could not reach {SpeedTest.Endpoint}: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _speedTestRunning, 0);
        }
    }

    private int _speedTestRunning;
    private DateTime _nextHourly = DateTime.Now.AddMinutes(3);

    /// <summary>The weekly digest card's lines and when they were made (shown for two days, or until closed).</summary>
    public (DateTime Made, IReadOnlyList<string> Lines)? Digest { get; private set; }

    /// <summary>For --text-check and previews: a digest to show.</summary>
    public void ShowDigest(IReadOnlyList<string> lines) => Digest = (DateTime.Now, lines);

    /// <summary>Mondays from 06:00 (or a week since the last one): the week in a few lines, as a card and a History entry.</summary>
    private void MaybeDigest(Settings settings, DateTime now)
    {
        if (settings.LastDigest is not { } last)
        {
            settings.LastDigest = now; // the first week starts now
            SaveSettings(settings);
            return;
        }
        bool monday = now.DayOfWeek == DayOfWeek.Monday && now.Hour >= 6 && last.Date < now.Date;
        if (!monday && now - last < TimeSpan.FromDays(8)) return;
        var from = now.AddDays(-7);
        var lines = Insights.WeeklyDigest(
            History.Games(500).Where(g => g.Start >= from).ToList(),
            History.Minutes(from),
            History.JobRuns(limit: 5000, since: from),
            History.SpeedTests(from),
            History.EventsSince(from, "alert"));
        settings.LastDigest = now;
        SaveSettings(settings);
        if (lines.Count == 0) return;
        Digest = (now, lines);
        History.AddEvent("digest", "Your week", string.Join(" ", lines));
    }

    /// <summary>The optional daily speed test: once a day after its time, only when you've been away 10 minutes and no game runs.</summary>
    private void MaybeDailySpeedTest(Settings settings, DateTime now)
    {
        if (settings.DailySpeedTest is not { } at || !TimeSpan.TryParse(at, out var time) || now.TimeOfDay < time) return;
        if (settings.LastDailySpeedTest?.Date == now.Date || CurrentGame is not null || Volatile.Read(ref _speedTestRunning) == 1) return;
        if (UserIdle() < TimeSpan.FromMinutes(10)) return;
        settings.LastDailySpeedTest = now;
        SaveSettings(settings);
        Log.Write("daily speed test");
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await SpeedTestAsync(null, cts.Token).ConfigureAwait(false);
        });
    }

    private readonly Dictionary<(string Job, string Kind), TimeSpan?> _usual = [];

    /// <summary>How long a job's runs of this kind usually take, from its last finished runs (cached until the next one ends).</summary>
    private TimeSpan? UsualDuration(string job, string kind)
    {
        lock (_usual)
        {
            if (_usual.TryGetValue((job, kind), out var known)) return known;
            var usual = JobText.Usual(History.JobRuns(job, kind, 20).Where(r => r.Succeeded).Select(r => r.Duration));
            _usual[(job, kind)] = usual;
            return usual;
        }
    }

    /// <summary>"usually 2h 10m · about 40 min left" for a running job's card, or null before it has a usual time.</summary>
    public string? JobProgress(string label, DateTime? started)
    {
        if (started is null) return null;
        int colon = label.IndexOf(": ", StringComparison.Ordinal);
        string job = colon > 0 ? label[..colon] : label;
        return JobText.Progress(DateTime.Now - started.Value, UsualDuration(job, JobText.RunKind(label)));
    }

    /// <summary>
    /// The program behind at least half of the traffic before a speed test, from the sensor helper's per-program
    /// network figures (null without them, or when no one program stands out).
    /// </summary>
    public static string? BusiestOnTheLine(Snapshot? before, double backgroundMbps)
    {
        if (before is not { IoTraced: true } || backgroundMbps < 2) return null;
        var top = before.AllWorkloads.Where(w => w.Kind != WorkloadKind.Lighthouse).MaxBy(w => w.NetDownMbps + w.NetUpMbps);
        return top is not null && top.NetDownMbps + top.NetUpMbps >= 0.5 * backgroundMbps ? top.Label : null;
    }

    private static TimeSpan UserIdle()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        ulong now = NativeMethods.GetTickCount64();
        uint low = (uint)now;
        return TimeSpan.FromMilliseconds(unchecked(low - info.dwTime));
    }

    private static string CpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string name = (key?.GetValue("ProcessorNameString") as string ?? "CPU").Trim();
        name = name.Replace("AMD ", "").Replace("Intel(R) Core(TM) ", "");
        int cut = name.IndexOf(" with ", StringComparison.Ordinal);
        if (cut > 0) name = name[..cut];
        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+-Core Processor$", "").Trim();
    }

    private static string ShortGpuName(string? name) =>
        name is null ? "GPU" : name.Replace("AMD Radeon ", "").Replace("NVIDIA GeForce ", "").Trim();

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

    private static double InstalledRamGb() =>
        GetPhysicallyInstalledSystemMemory(out ulong kb) ? Math.Round(kb / 1024.0 / 1024.0) : 0;

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(3000); // --text-check builds an engine it never starts
        _counters?.Dispose();
        _hardware?.Dispose();
        _jobs.Dispose();
        History.Dispose();
    }
}
