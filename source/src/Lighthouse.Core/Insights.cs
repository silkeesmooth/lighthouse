using Lighthouse.Core.Health;
using Lighthouse.Core.History;
using Lighthouse.Core.Tracking;

namespace Lighthouse.Core;

/// <summary>An internet plan's speeds, from settings ("internetPlan": { "downMbps": 500, "upMbps": 50 }).</summary>
public sealed record InternetPlan(double DownMbps, double UpMbps);

/// <summary>Fixed rules over the history: the weekly digest, the cooling trend, a speed test against the plan.</summary>
public static class Insights
{
    /// <summary>"6% of your plan down (500 Mbps), 32% up (50 Mbps)", or null without a plan.</summary>
    public static string? PlanShare(double downMbps, double upMbps, InternetPlan? plan)
    {
        if (plan is null || plan.DownMbps <= 0) return null;
        string down = $"{100 * downMbps / plan.DownMbps:0}% of your plan down ({plan.DownMbps:0} Mbps)";
        return plan.UpMbps > 0 ? $"{down}, {100 * upMbps / plan.UpMbps:0}% up ({plan.UpMbps:0} Mbps)" : down;
    }

    /// <summary>
    /// The week in a few lines, each left out when there is nothing to say: gaming, temperature peaks, job runs
    /// (with failures), internet speed, alerts.
    /// </summary>
    public static List<string> WeeklyDigest(IReadOnlyList<GameSession> games, IReadOnlyList<MinuteRow> minutes,
        IReadOnlyList<JobRun> jobs, IReadOnlyList<SpeedRun> speeds, IReadOnlyList<EventRow> alerts)
    {
        var lines = new List<string>();
        if (games.Count > 0)
        {
            var total = TimeSpan.FromTicks(games.Sum(g => g.Duration.Ticks));
            var most = games.GroupBy(g => g.Game).Select(g => (Game: g.Key, Time: TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks))))
                .MaxBy(g => g.Time);
            lines.Add($"Gaming: {Durations.Format(total)} across {games.Count} session{(games.Count == 1 ? "" : "s")}" +
                      (games.Select(g => g.Game).Distinct().Count() > 1 ? $", most of it {most.Game} ({Durations.Format(most.Time)})." : $" of {most.Game}."));
        }
        double? cpu = minutes.Max(m => m.CpuTemp), gpu = minutes.Max(m => m.GpuTemp), hotspot = minutes.Max(m => m.GpuHotspot);
        if (cpu is not null || gpu is not null)
            lines.Add("Peaks: " + string.Join(", ", new[]
            {
                cpu is { } c ? $"CPU {c:0}°C" : null,
                gpu is { } g ? $"GPU {g:0}°C" + (hotspot is { } h ? $" (hotspot {h:0}°C)" : "") : null,
            }.Where(p => p is not null)) + ".");
        foreach (var job in jobs.GroupBy(j => j.Job).OrderByDescending(g => g.Count()))
        {
            var failed = job.Where(r => !r.Succeeded).ToList();
            string failures = failed.Count == 0 ? ", all finished"
                : $", {failed.Count} stopped ({string.Join(", ", failed.GroupBy(f => f.Exit).Select(f => f.Key is { } x ? $"exit {x}" : "no exit code"))})";
            lines.Add($"{job.Key}: {job.Count()} run{(job.Count() == 1 ? "" : "s")}{failures}.");
        }
        if (speeds.Count > 0)
        {
            double low = speeds.Min(s => s.DownMbps), high = speeds.Max(s => s.DownMbps);
            string range = Math.Abs(high - low) < 1 ? $"{high:0} Mbps" : $"{low:0} to {high:0} Mbps";
            lines.Add($"Internet: {range} down across {speeds.Count} test{(speeds.Count == 1 ? "" : "s")}.");
        }
        if (alerts.Count > 0)
            lines.Add($"Alerts: {alerts.Count} ({string.Join(", ", alerts.GroupBy(a => a.Title).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"))}).");
        return lines;
    }

    private static readonly (double From, double To, string Name)[] LoadBands = [(10, 30, "light"), (30, 60, "medium"), (60, 101, "heavy")];

    /// <summary>
    /// Whether the CPU runs hotter than it used to at the same load: the median CPU temperature in each load band
    /// over the last 7 days against days 30 to 60 ago. Each band needs 60 minutes in both windows; with none, the
    /// check is left out. It fails at 5 °C or more in any band. A fixed rule, not a guess.
    /// </summary>
    public static HealthResult? CoolingTrend(IReadOnlyList<MinuteRow> minutes, DateTime now)
    {
        var recent = minutes.Where(m => m.Time >= now.AddDays(-7) && m.CpuTemp is not null).ToList();
        var baseline = minutes.Where(m => m.Time >= now.AddDays(-60) && m.Time < now.AddDays(-30) && m.CpuTemp is not null).ToList();
        (string Band, double Then, double Now)? worst = null;
        foreach (var (from, to, name) in LoadBands)
        {
            var a = recent.Where(m => m.Cpu >= from && m.Cpu < to).Select(m => m.CpuTemp!.Value).ToList();
            var b = baseline.Where(m => m.Cpu >= from && m.Cpu < to).Select(m => m.CpuTemp!.Value).ToList();
            if (a.Count < 60 || b.Count < 60) continue;
            var band = (name, Median(b), Median(a));
            if (worst is null || band.Item3 - band.Item2 > worst.Value.Now - worst.Value.Then) worst = band;
        }
        if (worst is not { } w) return null;
        double rise = w.Now - w.Then;
        bool ok = rise < 5;
        return new HealthResult("cooling", "Cooling", ok, ok
            ? $"At the same load the CPU runs as cool as a month ago ({w.Now:0}°C under {w.Band} load)."
            : $"At the same load the CPU runs {rise:0} °C hotter than a month ago ({w.Then:0} to {w.Now:0}°C under {w.Band} load). " +
              "Dust in the cooler, a slowing fan or a warmer room are the usual causes.");
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
