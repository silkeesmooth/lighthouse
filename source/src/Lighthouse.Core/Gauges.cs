using Lighthouse.Core.Model;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Core;

/// <summary>
/// What one gauge shows: the ring's fill and its text, the big line, the small line under it, and the palette key for
/// its colour ("Accent", "Warn", "Hot", "Muted"). <see cref="Short"/> is the line under the mini monitor's gauge,
/// which has a quarter of 268 px and no small line: the big line unless that is too wide.
/// </summary>
public sealed record GaugeFace(double Fraction, string Ring, string Main, string Detail, string Accent)
{
    public string Short { get; init; } = Main;
}

/// <summary>
/// The four gauges (flyout and mini monitor). A figure that couldn't be read says why in words instead of showing
/// 0% or a dash, which look like readings: a stopped GPU driver, no sensor helper, or Windows giving no number.
/// </summary>
public static class Gauges
{
    /// <summary>A non-breaking space: keeps a number with its unit ("335 W") when a line wraps.</summary>
    public const string Nb = " ";
    public const string Stopped = "Stopped";
    public const string NoTemp = "No temp";
    public const string NoReading = "No reading";
    private const string NoData = "No data";
    private const string NoRing = "—";

    public static GaugeFace Cpu(Snapshot s)
    {
        string detail = Join(Watts(s.CpuPower), s.CpuClockMhz is { } mhz ? $"{mhz / 1000:0.0}{Nb}GHz" : null);
        if (s.CpuTemp is null && s.SensorError is { } error) detail = Join(SensorsShort(error), detail);
        string accent = HeatKey(TrayIconRenderer.CpuHeat(s.CpuTemp));
        if (!s.CpuLoadRead) return new(0, NoRing, Deg(s.CpuTemp) ?? NoTemp, Join("no load reading", detail), accent);
        return new(s.CpuLoad / 100, Ring(s.CpuLoad / 100), Deg(s.CpuTemp) ?? NoTemp, detail, accent);
    }

    public static GaugeFace Gpu(Snapshot s)
    {
        if (s.GpuStopped is { } card) return new(0, NoRing, Stopped, DriverCode(card.Problem), "Warn");
        string detail = Join(s.GpuHotspot is { } hs ? $"hotspot{Nb}{hs:0}°C" : null, Watts(s.GpuPower));
        string accent = HeatKey(TrayIconRenderer.GpuHeat(s.GpuTemp, s.GpuHotspot));
        if (!s.GpuLoadRead) return new(0, NoRing, Deg(s.GpuTemp) ?? NoTemp, Join("no load reading", detail), accent);
        return new(s.GpuLoad / 100, Ring(s.GpuLoad / 100), Deg(s.GpuTemp) ?? NoTemp, detail, accent);
    }

    public static GaugeFace Ram(Snapshot s)
    {
        if (s.RamTotalGb <= 0) return new(0, NoRing, NoReading, "Windows gave no figure", "Muted") { Short = NoData };
        double f = s.RamUsedGb / s.RamTotalGb;
        return new(f, Ring(f), $"{s.RamUsedGb:0.0}{Nb}GB", Join($"of{Nb}{s.RamTotalGb:0}{Nb}GB", $"commit{Nb}{s.CommitPct:0}%"), "Accent");
    }

    public static GaugeFace Vram(Snapshot s)
    {
        if (s.GpuStopped is { } card) return new(0, NoRing, Stopped, DriverCode(card.Problem), "Warn");
        if (!s.GpuLoadRead || s.VramTotalGb <= 0)
            return new(0, NoRing, NoReading, s.VramTotalGb > 0 ? $"of{Nb}{s.VramTotalGb:0}{Nb}GB" : "no GPU found", "Muted") { Short = NoData };
        double f = s.VramUsedGb / s.VramTotalGb;
        return new(f, Ring(f), $"{s.VramUsedGb:0.0}{Nb}GB", $"of{Nb}{s.VramTotalGb:0}{Nb}GB", "Accent");
    }

    /// <summary>"GPU 97% · 74°C", or "GPU driver stopped", for one-line summaries (the tray tooltip, the game card).</summary>
    public static string GpuLine(Snapshot s, string space = " ")
    {
        if (s.GpuStopped is not null) return $"GPU driver{space}stopped";
        string load = s.GpuLoadRead ? $"GPU{space}{s.GpuLoad:0}%" : "GPU";
        return s.GpuTemp is { } t ? $"{load} · {t:0}°C" : load;
    }

    public static string HeatKey(Heat heat) => heat switch
    {
        Heat.Hot => "Hot",
        Heat.Warm => "Warn",
        _ => "Accent",
    };

    /// <summary>Under "Stopped": what stopped, and the code to look up ("Driver stopped" itself is too wide for a tile).</summary>
    private static string DriverCode(int code) => $"GPU driver, Device Manager code{Nb}{code}";

    /// <summary>The short form of why the admin sensors are missing; the flyout's subline carries the whole reason.</summary>
    private static string SensorsShort(string error) =>
        error == "the sensor helper isn't running" ? "sensor helper not running" : "sensors unavailable";

    private static string Ring(double fraction) => $"{Math.Clamp(fraction, 0, 1) * 100:0}%";
    private static string? Deg(double? v) => v is { } x ? $"{x:0}°C" : null;
    private static string? Watts(double? v) => v is { } x ? $"{x:0}{Nb}W" : null;
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
