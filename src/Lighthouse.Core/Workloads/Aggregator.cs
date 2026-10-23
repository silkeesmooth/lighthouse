using Lighthouse.Core.Model;

namespace Lighthouse.Core.Workloads;

/// <summary>One process's use over the last interval, already assigned to its workload.</summary>
public sealed record ProcessUsage(
    int Pid,
    string Name,
    Resolved Workload,
    double CpuRaw,
    long PrivateWorkingSet,
    long SharedWorkingSet,
    double VramBytes,
    IReadOnlyDictionary<int, double> GpuByEngine,
    DateTime Started,
    double DiskBps = 0,
    double NetDownBps = 0,
    double NetUpBps = 0);

public sealed record WorkloadTable(
    IReadOnlyList<WorkloadUsage> Top,
    WorkloadUsage Rest,
    IReadOnlyList<WorkloadUsage> All,
    WorkloadUsage? Self)
{
    public IReadOnlyList<WorkloadUsage> TopIo { get; init; } = [];
    public WorkloadUsage? RestIo { get; init; }
}

/// <summary>The disk and network totals the Disk & network rows add up to, in bytes per second.</summary>
public sealed record IoTotals(bool Traced, double DiskBps, double NetDownBps, double NetUpBps);

/// <summary>
/// Sums processes into workloads so that the rows plus "Everything else" add up to exactly the totals
/// on the tiles (CPU, RAM, VRAM). GPU load is the busiest engine, which does not add up, so the rest shows "—".
/// </summary>
public static class Aggregator
{
    private const double Gb = 1024.0 * 1024 * 1024;

    public static WorkloadTable Aggregate(IReadOnlyList<ProcessUsage> processes, double cpuTotalPct, double ramUsedGb,
        double vramUsedGb, int topCount = 6, IoTotals? io = null)
    {
        double rawTotal = processes.Sum(p => p.CpuRaw);

        var all = processes
            .GroupBy(p => p.Workload.Key)
            .Select(g =>
            {
                var first = g.First().Workload;
                var engines = new Dictionary<int, double>();
                foreach (var p in g)
                    foreach (var (engine, value) in p.GpuByEngine)
                        engines[engine] = engines.GetValueOrDefault(engine) + value;
                int sessions = g.Count(p => p.Name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase));
                string label = first.Kind == WorkloadKind.ClaudeCode && sessions > 1 ? $"{first.Label} ({sessions})" : first.Label;
                var root = g.FirstOrDefault(p => p.Pid == first.RootPid) ?? g.MinBy(p => p.Started)!;
                return new WorkloadUsage(
                    Key: first.Key,
                    Label: label,
                    Kind: first.Kind,
                    CpuPct: rawTotal > 0 ? cpuTotalPct * g.Sum(p => p.CpuRaw) / rawTotal : 0,
                    GpuPct: Math.Min(100, engines.Count == 0 ? 0 : engines.Values.Max()),
                    RamGb: g.Sum(p => RamOf(p)) / Gb,
                    VramGb: g.Sum(p => p.VramBytes) / Gb,
                    Processes: g.Count(),
                    Started: root.Started,
                    DiskMBps: g.Sum(p => p.DiskBps) / 1e6,
                    NetDownMbps: g.Sum(p => p.NetDownBps) * 8 / 1e6,
                    NetUpMbps: g.Sum(p => p.NetUpBps) * 8 / 1e6);
            })
            .OrderByDescending(w => Score(w, ramUsedGb, vramUsedGb))
            .ToList();

        var self = all.FirstOrDefault(w => w.Kind == WorkloadKind.Lighthouse);
        var top = all
            .Where(w => w.Kind != WorkloadKind.Lighthouse
                        && (w.CpuPct >= 0.05 || w.GpuPct >= 0.5 || w.RamGb >= 0.05 || w.VramGb >= 0.05))
            .Take(topCount)
            .ToList();

        top = Fit(top, ramUsedGb, w => w.RamGb, (w, v) => w with { RamGb = v });
        top = Fit(top, vramUsedGb, w => w.VramGb, (w, v) => w with { VramGb = v });

        var rest = new WorkloadUsage(
            Key: "rest",
            Label: "Everything else",
            Kind: WorkloadKind.Windows,
            CpuPct: Math.Max(0, cpuTotalPct - top.Sum(w => w.CpuPct)),
            GpuPct: double.NaN,
            RamGb: Math.Max(0, ramUsedGb - top.Sum(w => w.RamGb)),
            VramGb: Math.Max(0, vramUsedGb - top.Sum(w => w.VramGb)),
            Processes: processes.Count - top.Sum(w => w.Processes));
        var topKeys = top.Select(w => w.Key).ToHashSet();
        double otherApps = processes.Where(p => !topKeys.Contains(p.Workload.Key)).Sum(p => RamOf(p)) / Gb;
        otherApps = Math.Min(otherApps, rest.RamGb);
        rest = rest with
        {
            Note = $"{rest.Processes} other processes hold {otherApps:0.0} GB; Windows, drivers and caches hold {rest.RamGb - otherApps:0.0} GB.",
        };

        var (topIo, restIo) = IoRows(all, processes, cpuTotalPct, io, topCount);
        return new WorkloadTable(top, rest, all, self) { TopIo = topIo, RestIo = restIo };
    }

    /// <summary>
    /// The busiest rows by disk and network, and "Everything else" as what is left of each total: the trace's (or the
    /// counters') disk sum over every process, and the network adapters' totals, which also hold protocol overhead
    /// and traffic no program is named for.
    /// </summary>
    private static (List<WorkloadUsage> Rows, WorkloadUsage Remainder) IoRows(List<WorkloadUsage> all, IReadOnlyList<ProcessUsage> processes,
        double cpuTotalPct, IoTotals? io, int topCount)
    {
        double diskTotal = (io?.DiskBps ?? processes.Sum(p => p.DiskBps)) / 1e6;
        double downTotal = (io?.NetDownBps ?? 0) * 8 / 1e6, upTotal = (io?.NetUpBps ?? 0) * 8 / 1e6;
        var top = all
            .Where(w => w.Kind != WorkloadKind.Lighthouse && (w.DiskMBps >= 0.05 || w.NetDownMbps >= 0.05 || w.NetUpMbps >= 0.05))
            .OrderByDescending(w => w.DiskMBps * 8 + w.NetDownMbps + w.NetUpMbps)
            .Take(topCount)
            .ToList();
        top = Fit(top, diskTotal, w => w.DiskMBps, (w, v) => w with { DiskMBps = v });
        if (io is { NetDownBps: > 0 }) top = Fit(top, downTotal, w => w.NetDownMbps, (w, v) => w with { NetDownMbps = v });
        if (io is { NetUpBps: > 0 }) top = Fit(top, upTotal, w => w.NetUpMbps, (w, v) => w with { NetUpMbps = v });
        var rest = new WorkloadUsage(
            Key: "rest",
            Label: "Everything else",
            Kind: WorkloadKind.Windows,
            CpuPct: Math.Max(0, cpuTotalPct - top.Sum(w => w.CpuPct)),
            GpuPct: double.NaN,
            RamGb: 0,
            VramGb: 0,
            Processes: processes.Count - top.Sum(w => w.Processes),
            Note: io is { Traced: true }
                ? "Other programs, Windows itself, and network protocol overhead no program is named for."
                : "Other programs and Windows. Network per program needs the sensor helper, so all of it is here.",
            DiskMBps: Math.Max(0, diskTotal - top.Sum(w => w.DiskMBps)),
            NetDownMbps: Math.Max(0, downTotal - top.Sum(w => w.NetDownMbps)),
            NetUpMbps: Math.Max(0, upTotal - top.Sum(w => w.NetUpMbps)));
        return (top, rest);
    }

    /// <summary>
    /// Private working set, like Task Manager. The local LLM also gets its shared pages: llama-server maps the
    /// model file (about 18 GB here), which is shareable memory only it uses, and leaving it out would bury the
    /// LLM's real footprint in "Everything else".
    /// </summary>
    private static long RamOf(ProcessUsage p) =>
        p.PrivateWorkingSet + (p.Workload.Kind == WorkloadKind.LocalLlm ? Math.Max(0, p.SharedWorkingSet) : 0);

    private static double Score(WorkloadUsage w, double ramUsed, double vramUsed) =>
        w.CpuPct + w.GpuPct + 50 * w.RamGb / Math.Max(ramUsed, 0.001) + 50 * w.VramGb / Math.Max(vramUsed, 0.001);

    /// <summary>If the rows claim more than the total (counters read a moment apart), scale them down to fit.</summary>
    private static List<WorkloadUsage> Fit(List<WorkloadUsage> rows, double total, Func<WorkloadUsage, double> get,
        Func<WorkloadUsage, double, WorkloadUsage> set)
    {
        double sum = rows.Sum(get);
        if (sum <= total || sum <= 0) return rows;
        double k = total / sum;
        return rows.Select(w => set(w, get(w) * k)).ToList();
    }
}
