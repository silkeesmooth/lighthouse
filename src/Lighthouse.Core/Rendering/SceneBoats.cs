using Lighthouse.Core.Model;

namespace Lighthouse.Core.Rendering;

/// <summary>
/// Which programs sail as boats: every watched job, and any other program while it works hard (about a core of
/// CPU, 10% of the GPU, or 5 Mbps of network). A program stays on the water for <see cref="Linger"/> after it calms
/// down, so a boat doesn't blink in and out with a load that wobbles. At most <see cref="Most"/>, jobs first, then
/// the busiest. The game is the ship and the local LLM the moon, so they never sail; nor do Windows and Lighthouse.
/// </summary>
public sealed class SceneBoats
{
    public const int Most = 6;
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTime> _lastBusy = [];

    public List<WorkloadUsage> Pick(IReadOnlyList<WorkloadUsage> all, DateTime now, int logicalCpus)
    {
        double oneCore = 100.0 / Math.Max(1, logicalCpus);
        var sailing = new List<(WorkloadUsage Work, double Score)>();
        foreach (var w in all)
        {
            if (w.Kind is WorkloadKind.Game or WorkloadKind.LocalLlm or WorkloadKind.Windows or WorkloadKind.Lighthouse) continue;
            double gpu = double.IsNaN(w.GpuPct) ? 0 : w.GpuPct;
            double score = w.CpuPct / oneCore + gpu / 10 + (w.NetDownMbps + w.NetUpMbps) / 5;
            bool job = w.Kind == WorkloadKind.Job;
            if (job || score >= 1) _lastBusy[w.Key] = now;
            if (job || (_lastBusy.TryGetValue(w.Key, out var busy) && now - busy <= Linger))
                sailing.Add((w, job ? double.MaxValue : score));
        }
        var present = all.Select(w => w.Key).ToHashSet();
        foreach (string gone in _lastBusy.Keys.Where(k => !present.Contains(k) || now - _lastBusy[k] > Linger).ToList()) _lastBusy.Remove(gone);
        return sailing.OrderByDescending(s => s.Score).Take(Most).Select(s => s.Work).ToList();
    }
}
