using Lighthouse.Core.Alerts;
using Lighthouse.Core.Model;

namespace Lighthouse.Core;

/// <summary>The one sentence at the top of the flyout. Fixed rules, no LLM: it must be right every second.</summary>
public static class Headline
{
    public static string Build(Snapshot s, IReadOnlyList<Notice> active, DateTime now)
    {
        if (active.Where(a => a.Severity >= Severity.Warning).MaxBy(a => a.Severity) is { } alert)
            return alert.Message;

        if (s.Game is { } game)
        {
            string temp = s.GpuTemp is { } t ? $", {t:0}°C" : "";
            string ccd = s.Ccd switch
            {
                CcdState.OtherParked => ", V-Cache CCD active",
                CcdState.BothActive when s.GpuLoad >= 50 => ", both CCDs active",
                _ => "",
            };
            return $"{game.Label}: GPU {s.GpuLoad:0}%{temp}{ccd}.";
        }

        var runs = s.AllWorkloads.Where(w => w.Kind == WorkloadKind.Job).ToList();
        if (runs.Count > 0)
        {
            var run = runs.MaxBy(w => w.CpuPct)!;
            int colon = run.Label.IndexOf(": ", StringComparison.Ordinal);
            string what = colon > 0 ? run.Label.Remove(colon, 1) : run.Label; // "Import: load" -> "Import load"
            string more = runs.Count > 1 ? $" (+{runs.Count - 1} more)" : "";
            return $"{what} is using {CoresText(run.CpuPct)}, {Elapsed(run.Started, now)} in{more}.";
        }

        if (s.LocalLlm is { } llm && llm.GpuPct >= 30)
            return $"The local LLM is thinking: GPU {s.GpuLoad:0}%.";

        if (s.CpuLoad >= 50 || s.GpuLoad >= 50)
        {
            var top = s.CpuLoad >= s.GpuLoad
                ? s.Workloads.MaxBy(w => w.CpuPct)
                : s.Workloads.MaxBy(w => w.GpuPct);
            if (top is not null)
                return s.CpuLoad >= s.GpuLoad
                    ? $"Busy: {top.Label} is using {top.CpuPct:0}% CPU."
                    : $"Busy: {top.Label} is using the GPU ({s.GpuLoad:0}%).";
        }

        if (s.LocalLlm is { } idleLlm && idleLlm.VramGb >= 1 && s.VramTotalGb > 0)
            return $"Calm. The local LLM holds {idleLlm.VramGb:0.0} of {s.VramTotalGb:0} GB VRAM.";

        return "Calm.";
    }

    public static double Cores(double cpuPct) => cpuPct / 100 * Environment.ProcessorCount;

    /// <summary>"1 core", "1.5 cores", "12 cores".</summary>
    public static string CoresText(double cpuPct, string space = " ")
    {
        string n = Cores(cpuPct).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        return n == "1" ? $"1{space}core" : $"{n}{space}cores";
    }

    public static string Elapsed(DateTime? since, DateTime now)
    {
        if (since is null) return "just";
        var span = now - since.Value;
        if (span.TotalMinutes < 1) return "seconds";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:0}m";
        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }
}
