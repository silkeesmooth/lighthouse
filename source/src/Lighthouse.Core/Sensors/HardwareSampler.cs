using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace Lighthouse.Core.Sensors;

public sealed record HardwareReading
{
    public double? CpuTemp { get; init; }
    public IReadOnlyList<double> CcdTemps { get; init; } = [];
    public double? CpuPower { get; init; }
    public double? CpuClockMhz { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuMemTemp { get; init; }
    public double? GpuPower { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? GpuMemClockMhz { get; init; }
    public double? DimmTemp { get; init; }
    public double? NvmeTemp { get; init; }
    public double? NvmeWearPct { get; init; }
    public double? NvmeSparePct { get; init; }
}

/// <summary>
/// LibreHardwareMonitor sensors. CPU, DIMM and SSD readings need admin and the PawnIO driver; without
/// them those values are null and everything else keeps working. GPU sensors come from the AMD driver.
/// </summary>
public sealed class HardwareSampler : IDisposable
{
    private readonly Computer? _computer;
    private readonly IHardware? _mainGpu;
    private DateTime _lastSlow = DateTime.MinValue;
    private HardwareReading _slow = new();

    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public string? Error { get; }

    public HardwareSampler(string? gpuName)
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
            };
            _computer.Open();
            // The GPU DXGI calls main (the graphics card, not the CPU's iGPU); else the first one listed.
            var gpus = _computer.Hardware
                .Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                .ToList();
            _mainGpu = gpus.FirstOrDefault(h => gpuName is not null
                           && (h.Name.Contains(gpuName, StringComparison.OrdinalIgnoreCase)
                               || gpuName.Contains(h.Name, StringComparison.OrdinalIgnoreCase)))
                       ?? gpus.FirstOrDefault();
        }
        catch (Exception e)
        {
            Error = e.Message;
            _computer = null;
        }
    }

    public HardwareReading Sample()
    {
        if (_computer is null) return new HardwareReading();
        var reading = new HardwareReading();
        // DIMM and SSD sensors change slowly and cost SMBus/SMART reads: every 30 s is plenty.
        bool slowDue = DateTime.Now - _lastSlow > TimeSpan.FromSeconds(30);
        double? dimmMax = null;

        foreach (IHardware hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    Update(hw);
                    reading = reading with
                    {
                        CpuTemp = Find(hw, SensorType.Temperature, "Tctl/Tdie") ?? Find(hw, SensorType.Temperature, "Tdie")
                                  ?? Find(hw, SensorType.Temperature, "Package") ?? Find(hw, SensorType.Temperature, "Core"),
                        CcdTemps = hw.Sensors
                            .Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("CCD", StringComparison.Ordinal)
                                        && char.IsDigit(s.Name.ElementAtOrDefault(3)) && s.Value is > 0)
                            .OrderBy(s => s.Name, StringComparer.Ordinal)
                            .Select(s => (double)s.Value!.Value)
                            .ToList(),
                        CpuPower = Find(hw, SensorType.Power, "Package"),
                        // The busiest core's effective clock: "Core #n" alone is the requested clock, 5.5 GHz even when asleep.
                        CpuClockMhz = hw.Sensors
                            .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.Ordinal)
                                        && s.Name.EndsWith("(Effective)", StringComparison.Ordinal) && s.Value is > 0)
                            .Select(s => (double?)s.Value).DefaultIfEmpty().Max(),
                    };
                    break;

                case HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel:
                    if (hw != _mainGpu) break;
                    Update(hw);
                    reading = reading with
                    {
                        GpuTemp = Find(hw, SensorType.Temperature, "GPU Core") ?? Find(hw, SensorType.Temperature, "Core"),
                        GpuHotspot = Find(hw, SensorType.Temperature, "Hot Spot"),
                        GpuMemTemp = Find(hw, SensorType.Temperature, "Memory"),
                        GpuPower = Find(hw, SensorType.Power, "Package")
                                   ?? hw.Sensors.Where(s => s.SensorType == SensorType.Power).Select(s => (double?)s.Value).Max(),
                        GpuFanRpm = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).Select(s => (double?)s.Value).Max(),
                        GpuMemClockMhz = Find(hw, SensorType.Clock, "Memory"),
                    };
                    break;

                case HardwareType.Memory when slowDue:
                    Update(hw);
                    // Only "DIMM #n": the SPD hub also reports its limits (55, 85) as temperature sensors.
                    double? dimm = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value > 0
                                                         && s.Name.StartsWith("DIMM", StringComparison.Ordinal))
                        .Select(s => (double?)s.Value).Max();
                    if (dimm is not null) dimmMax = Math.Max(dimm.Value, dimmMax ?? 0);
                    break;

                case HardwareType.Storage when slowDue:
                    Update(hw);
                    double? temp = Find(hw, SensorType.Temperature, "Composite") ?? Find(hw, SensorType.Temperature, "Temperature");
                    if (temp is null) break;
                    _slow = _slow with
                    {
                        NvmeTemp = temp,
                        NvmeWearPct = Exact(hw, SensorType.Level, "Percentage Used"),
                        NvmeSparePct = Exact(hw, SensorType.Level, "Available Spare"), // not "... Threshold"
                    };
                    break;
            }
        }

        if (slowDue)
        {
            _lastSlow = DateTime.Now;
            _slow = _slow with { DimmTemp = dimmMax };
        }
        return reading with
        {
            DimmTemp = _slow.DimmTemp,
            NvmeTemp = _slow.NvmeTemp,
            NvmeWearPct = _slow.NvmeWearPct,
            NvmeSparePct = _slow.NvmeSparePct,
        };
    }

    private static void Update(IHardware hw)
    {
        hw.Update();
        foreach (IHardware sub in hw.SubHardware) sub.Update();
    }

    /// <summary>The first sensor of a type whose name contains the text, skipping zeros (a sensor that was not read).</summary>
    private static double? Find(IHardware hw, SensorType type, string nameContains)
    {
        foreach (ISensor s in hw.Sensors.Concat(hw.SubHardware.SelectMany(h => h.Sensors)))
            if (s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase) && s.Value is { } v && v > 0)
                return v;
        return null;
    }

    /// <summary>A sensor by its exact name. Zero is a real value here (0% of the SSD's life used).</summary>
    private static double? Exact(IHardware hw, SensorType type, string name) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value;

    /// <summary>Every sensor LibreHardwareMonitor sees, for checking names against a real machine.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"elevated={IsElevated} error={Error ?? "none"}");
        if (_computer is null) return sb.ToString();
        foreach (IHardware hw in _computer.Hardware)
        {
            Update(hw);
            sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
            foreach (ISensor s in hw.Sensors.Concat(hw.SubHardware.SelectMany(h => h.Sensors)))
                sb.AppendLine($"  {s.SensorType,-12} {s.Name,-36} {s.Value?.ToString("0.##") ?? "-"}");
        }
        return sb.ToString();
    }

    public void Dispose() => _computer?.Close();
}
