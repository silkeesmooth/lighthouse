using Lighthouse.Core.Model;

namespace Lighthouse.Core.Alerts;

public enum Severity { Info, Warning, Critical }

public sealed record Notice(string Id, Severity Severity, string Title, string Message);

public sealed record Thresholds
{
    public double CpuTempWarn { get; init; } = 90;
    public double CpuTempCritical { get; init; } = 95; // Ryzen's limit; Intel's is 100
    public double GpuHotspotWarn { get; init; } = 105;
    public double GpuHotspotCritical { get; init; } = 110;
    public double GpuMemTempWarn { get; init; } = 100;
    public double CommitWarnPct { get; init; } = 90;
    public double NvmeTempWarn { get; init; } = 70;
    public double SustainSeconds { get; init; } = 20;
    public double CooldownMinutes { get; init; } = 30;
    public double RunawayMinutes { get; init; } = 15;
    public double AwayMinutes { get; init; } = 10;
    public double LeakGb { get; init; } = 2;
    public double LeakMinutes { get; init; } = 30;
    public double LlmVramContentionGb { get; init; } = 8;
}

public sealed record AlertContext(bool InGame, bool Paused, TimeSpan UserIdle);

/// <summary>
/// Turns snapshots into notices. A condition must hold for its whole sustain time, fires once, and re-arms
/// only after it clears and its cooldown passes. In a game or while paused only critical notices show;
/// suppressed ones are consumed, not queued, so nothing floods in when the game ends.
/// </summary>
public sealed class AlertEngine(Thresholds t, Func<DateTime>? clock = null)
{
    private sealed class State
    {
        public DateTime? Since;
        public DateTime? LastFired;
        public bool Armed = true;
    }

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);
    private readonly Dictionary<string, State> _states = [];
    private readonly Dictionary<string, DateTime> _busySince = [];
    private readonly Dictionary<string, Queue<(DateTime Time, double Gb)>> _ramHistory = [];
    private DateTime _lastRamSample = DateTime.MinValue;

    public Thresholds Thresholds => t;

    /// <summary>Conditions that have held for their sustain time right now (for the headline and the cards).</summary>
    public IReadOnlyList<Notice> Active { get; private set; } = [];

    public IReadOnlyList<Notice> Evaluate(Snapshot s, AlertContext ctx)
    {
        DateTime now = _now();
        var fire = new List<Notice>();
        var active = new List<Notice>();
        var current = Conditions(s, ctx, now).ToList();
        var currentIds = current.Select(c => c.Notice.Id).ToHashSet();

        foreach (var (notice, sustain) in current)
        {
            var st = _states.TryGetValue(notice.Id, out var existing) ? existing : _states[notice.Id] = new State();
            st.Since ??= now;
            if (now - st.Since.Value < sustain) continue;
            active.Add(notice);
            bool cooled = st.LastFired is null || now - st.LastFired.Value >= TimeSpan.FromMinutes(t.CooldownMinutes);
            if (!st.Armed || !cooled) continue;
            st.Armed = false;
            st.LastFired = now;
            bool quiet = (ctx.InGame || ctx.Paused) && notice.Severity < Severity.Critical;
            if (!quiet) fire.Add(notice);
        }

        foreach (var (id, st) in _states)
        {
            if (currentIds.Contains(id)) continue;
            st.Since = null;
            st.Armed = true;
        }

        Active = active;
        return fire;
    }

    private IEnumerable<(Notice Notice, TimeSpan Sustain)> Conditions(Snapshot s, AlertContext ctx, DateTime now)
    {
        var sustain = TimeSpan.FromSeconds(t.SustainSeconds);
        string topCpu = Top(s, w => w.CpuPct) is { } c ? $" {c.Label} is the biggest load ({c.CpuPct:0}% CPU)." : "";

        if (s.CpuTemp >= t.CpuTempCritical)
            yield return (new("cpu-temp-critical", Severity.Critical, "CPU at its limit",
                $"CPU at {s.CpuTemp:0}°C; its limit is {t.CpuTempCritical:0}°C.{topCpu}"), TimeSpan.FromSeconds(10));
        else if (s.CpuTemp >= t.CpuTempWarn)
            yield return (new("cpu-temp", Severity.Warning, "CPU is running hot",
                $"CPU has been at {s.CpuTemp:0}°C for {t.SustainSeconds:0} s.{topCpu}"), sustain);

        string gpuBy = (s.Game ?? Top(s, w => w.GpuPct)) is { } g ? $" {g.Label} is driving it." : "";
        if (s.GpuHotspot >= t.GpuHotspotCritical)
            yield return (new("gpu-hotspot-critical", Severity.Critical, "GPU at its limit",
                $"GPU hotspot at {s.GpuHotspot:0}°C.{gpuBy}"), TimeSpan.FromSeconds(10));
        else if (s.GpuHotspot >= t.GpuHotspotWarn)
            yield return (new("gpu-hotspot", Severity.Warning, "GPU is running hot",
                $"GPU hotspot has been at {s.GpuHotspot:0}°C for {t.SustainSeconds:0} s.{gpuBy}"), sustain);

        if (s.GpuMemTemp >= t.GpuMemTempWarn)
            yield return (new("gpu-mem-temp", Severity.Warning, "GPU memory is hot",
                $"GPU memory at {s.GpuMemTemp:0}°C.{gpuBy}"), sustain);

        if (s.CommitPct >= t.CommitWarnPct)
        {
            string who = Top(s, w => w.RamGb) is { } r ? $" {r.Label} holds {r.RamGb:0.0} GB." : "";
            yield return (new("memory", Severity.Warning, "Memory is nearly full",
                $"{s.CommitPct:0}% of memory is committed.{who}"), TimeSpan.FromSeconds(30));
        }

        if (s.NvmeTemp >= t.NvmeTempWarn)
            yield return (new("nvme-temp", Severity.Warning, "SSD is hot",
                $"The SSD is at {s.NvmeTemp:0}°C."), TimeSpan.FromSeconds(30));

        if (s.Game is { } game && s.LocalLlm is { } llm && llm.VramGb >= t.LlmVramContentionGb
            && s.VramTotalGb > 0 && s.VramUsedGb / s.VramTotalGb >= 0.9)
            yield return (new("llm-vram", Severity.Warning, $"{game.Label} is short of VRAM",
                $"The local LLM holds {llm.VramGb:0.0} GB of VRAM. " +
                (llm.Key == "llm:lmstudio" ? "Unload its model in LM Studio while you play." : "Open Lighthouse to free it while you play.")),
                TimeSpan.FromSeconds(5));

        foreach (var notice in Runaways(s, ctx, now)) yield return (notice, TimeSpan.Zero);
        foreach (var notice in Leaks(s, now)) yield return (notice, TimeSpan.Zero);
    }

    private IEnumerable<Notice> Runaways(Snapshot s, AlertContext ctx, DateTime now)
    {
        double oneCore = 100.0 / Environment.ProcessorCount;
        var busy = new HashSet<string>();
        foreach (var w in s.AllWorkloads)
        {
            // Work you started on purpose is busy by design: games, watched jobs and services, the LLM, Claude Code.
            if (w.Kind is WorkloadKind.Game or WorkloadKind.Job or WorkloadKind.Service or WorkloadKind.LocalLlm
                or WorkloadKind.ClaudeCode or WorkloadKind.Windows or WorkloadKind.Lighthouse) continue;
            if (w.CpuPct < oneCore) continue;
            busy.Add(w.Key);
            _busySince.TryAdd(w.Key, now);
            if (now - _busySince[w.Key] >= TimeSpan.FromMinutes(t.RunawayMinutes) && ctx.UserIdle >= TimeSpan.FromMinutes(t.AwayMinutes))
                yield return new($"runaway:{w.Key}", Severity.Warning, $"{w.Label} is busy on its own",
                    $"{w.Label} has used {w.CpuPct / oneCore:0.#} cores for {(now - _busySince[w.Key]).TotalMinutes:0} min while you were away.");
        }
        foreach (string key in _busySince.Keys.Where(k => !busy.Contains(k)).ToList()) _busySince.Remove(key);
    }

    private IEnumerable<Notice> Leaks(Snapshot s, DateTime now)
    {
        if (now - _lastRamSample >= TimeSpan.FromMinutes(1))
        {
            _lastRamSample = now;
            var present = new HashSet<string>();
            foreach (var w in s.AllWorkloads)
            {
                if (w.Kind is WorkloadKind.Game or WorkloadKind.LocalLlm or WorkloadKind.Lighthouse) continue;
                present.Add(w.Key);
                if (!_ramHistory.TryGetValue(w.Key, out var q)) _ramHistory[w.Key] = q = new();
                q.Enqueue((now, w.RamGb));
                while (q.Count > 0 && now - q.Peek().Time > TimeSpan.FromMinutes(t.LeakMinutes + 1)) q.Dequeue();
            }
            foreach (string key in _ramHistory.Keys.Where(k => !present.Contains(k)).ToList()) _ramHistory.Remove(key);
        }

        foreach (var (key, q) in _ramHistory)
        {
            var points = q.ToList();
            if (points.Count < 2 || points[^1].Time - points[0].Time < TimeSpan.FromMinutes(t.LeakMinutes)) continue;
            double growth = points[^1].Gb - points[0].Gb;
            bool steady = points.Zip(points.Skip(1)).All(pair => pair.Second.Gb >= pair.First.Gb - 0.05);
            if (growth < t.LeakGb || !steady) continue;
            string label = s.AllWorkloads.FirstOrDefault(w => w.Key == key)?.Label ?? key;
            yield return new($"leak:{key}", Severity.Warning, $"{label} keeps growing",
                $"{label} grew {growth:0.0} GB in {t.LeakMinutes:0} min and is still growing.");
        }
    }

    private static WorkloadUsage? Top(Snapshot s, Func<WorkloadUsage, double> by) =>
        s.AllWorkloads.Where(w => w.Kind != WorkloadKind.Lighthouse).MaxBy(by);
}
