using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.RegularExpressions;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;
using Lighthouse.Core.Sensors;
using Microsoft.Win32;

namespace Lighthouse.Core.Health;

public sealed record HealthResult(string Id, string Title, bool Ok, string Detail);

/// <summary>Things that quietly cost performance, checked at startup and once a day. A check that doesn't apply is left out.</summary>
public static partial class HealthChecks
{
    public static List<HealthResult> RunAll(HardwareReading? hardware, DateTime driverResetsSince, IReadOnlyList<Snapshot>? recent = null,
        string? gpuName = null)
    {
        var results = new List<HealthResult>();
        void Add(Func<HealthResult?> check)
        {
            try
            {
                if (check() is { } r) results.Add(r);
            }
            catch
            {
                // A check that cannot run is left out rather than reported as passing.
            }
        }

        Add(RamSpeedCheck);
        Add(X3dCheck);
        Add(RefreshRateCheck);
        Add(DiskSpaceCheck);
        Add(() => NvmeCheck(hardware));
        Add(() => DriverResetCheck(driverResetsSince));
        Add(() => GpuIdlePowerResult(recent ?? [], MonitorsOn(gpuName)));
        return results;
    }

    /// <summary>The kit's rated speed from its part number: CMH64GX5M2M6000Z30 -> 6000, KF560C30 -> 5600.</summary>
    public static int? RatedSpeed(string partNumber)
    {
        var four = FourDigitSpeed().Match(partNumber);
        if (four.Success && int.TryParse(four.Groups[1].Value, out int mt) && mt is >= 3200 and <= 12000) return mt;
        var kingston = KingstonSpeed().Match(partNumber);
        if (kingston.Success) return int.Parse(kingston.Groups[1].Value) * 100;
        return null;
    }

    public static HealthResult? RamSpeedResult(IEnumerable<(string Part, int Configured)> modules)
    {
        var list = modules.ToList();
        if (list.Count == 0) return null;
        int configured = list.Min(m => m.Configured);
        int? rated = list.Select(m => RatedSpeed(m.Part)).Where(r => r is not null).Min();
        if (rated is null || configured <= 0) return null;
        bool ok = configured >= rated.Value - 100;
        return new HealthResult("ram-speed", "Memory speed", ok, ok
            ? $"RAM runs at {configured} MT/s, its rated speed."
            : $"RAM runs at {configured} MT/s; the kit is rated {rated} MT/s. Turning on EXPO (AMD) or XMP (Intel) in the BIOS runs it at its rated speed.");
    }

    private static HealthResult? RamSpeedCheck()
    {
        using var searcher = new ManagementObjectSearcher("SELECT PartNumber, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
        var modules = searcher.Get().Cast<ManagementObject>()
            .Select(m => (((string?)m["PartNumber"])?.Trim() ?? "", Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0)));
        return RamSpeedResult(modules);
    }

    private static HealthResult? X3dCheck()
    {
        using var searcher = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='amd3dvcacheSvc'");
        var service = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        if (service is null) return null; // not an X3D dual-CCD machine
        bool running = (string?)service["State"] == "Running";
        bool gameBar = GameBarInstalled();
        bool ok = running && gameBar;
        string detail = ok
            ? "The 3D V-Cache driver is running and Xbox Game Bar is installed, so games get the V-Cache CCD."
            : !running
                ? "The AMD 3D V-Cache service is not running: games may land on the wrong CCD."
                : "Xbox Game Bar is missing. AMD's driver uses it to spot games and park CCD1.";
        return new HealthResult("x3d", "V-Cache scheduling", ok, detail);
    }

    private static bool GameBarInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return key?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static HealthResult? RefreshRateCheck()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return null;
        var mode = NewDevMode();
        if (!NativeMethods.EnumDisplaySettings(screen.DeviceName, -1 /* current */, ref mode)) return null;
        int current = mode.dmDisplayFrequency, width = mode.dmPelsWidth, height = mode.dmPelsHeight, best = current;
        var probe = NewDevMode();
        for (int i = 0; NativeMethods.EnumDisplaySettings(screen.DeviceName, i, ref probe); i++)
            if (probe.dmPelsWidth == width && probe.dmPelsHeight == height) best = Math.Max(best, probe.dmDisplayFrequency);
        bool ok = current >= best - 1;
        return new HealthResult("refresh", "Main display", ok, ok
            ? $"Main display at {width}x{height}, {current} Hz (its maximum)."
            : $"Main display runs at {current} Hz but supports {best} Hz (Settings > Display > Advanced display).");
    }

    private static NativeMethods.DEVMODE NewDevMode()
    {
        var mode = new NativeMethods.DEVMODE();
        mode.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>();
        return mode;
    }

    private static HealthResult DiskSpaceCheck()
    {
        var c = new DriveInfo("C");
        double freeGb = c.AvailableFreeSpace / 1e9, pct = 100.0 * c.AvailableFreeSpace / c.TotalSize;
        bool ok = pct >= 10;
        return new HealthResult("disk-space", "Disk space", ok, ok
            ? $"C: has {freeGb:0} GB free ({pct:0}%)."
            : $"C: is down to {freeGb:0} GB free ({pct:0}%).");
    }

    private static HealthResult? NvmeCheck(HardwareReading? hw)
    {
        if (hw?.NvmeWearPct is not { } wear) return null;
        bool ok = wear < 80 && (hw.NvmeSparePct ?? 100) >= 10;
        return new HealthResult("nvme", "SSD health", ok, ok
            ? $"The SSD has used {wear:0}% of its rated life."
            : $"The SSD has used {wear:0}% of its rated life (spare {hw.NvmeSparePct:0}%). Plan a replacement.");
    }

    private static HealthResult DriverResetCheck(DateTime since)
    {
        long ms = (long)Math.Max(1, (DateTime.Now - since).TotalMilliseconds);
        string xpath = $"*[System[Provider[@Name='Display'] and (EventID=4101) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
        int count = 0;
        DateTime? last = null;
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
        {
            using (e)
            {
                count++;
                last = e.TimeCreated;
            }
        }
        bool ok = count == 0;
        return new HealthResult("gpu-resets", "GPU driver", ok, ok
            ? "No GPU driver resets."
            : $"The GPU driver reset {count} time{(count == 1 ? "" : "s")}, last at {last:g}.");
    }

    /// <summary>
    /// A GPU whose memory never slows down at idle. Seen on an RDNA 3 card: two monitors on the card
    /// held its memory at 2487 MHz and 71 W at every refresh rate, even both at 60 Hz; with one monitor it idled
    /// at 23 W and 173 MHz. Judged on at least a minute of idle samples, by the median so a spike does not count.
    /// </summary>
    public static HealthResult? GpuIdlePowerResult(IReadOnlyList<Snapshot> recent, int monitorsOnGpu)
    {
        var idle = recent.Where(s => s.GpuLoad < 3 && s.GpuPower is not null).ToList();
        if (idle.Count < 60) return null;
        double power = idle.Select(s => s.GpuPower!.Value).Order().ElementAt(idle.Count / 2);
        double? clock = idle.Select(s => s.GpuMemClockMhz).LastOrDefault(c => c is not null);
        bool ok = power < 45;
        string clockText = clock is { } c ? $" with its memory clock at {c:0} MHz" : "";
        string why = monitorsOnGpu >= 2
            ? $" With {monitorsOnGpu} monitors on the graphics card its memory can't slow down between frames; " +
              "plugging one into the motherboard's video output usually fixes it."
            : "";
        return new HealthResult("gpu-idle-power", "GPU idle power", ok, ok
            ? $"The GPU idles at {power:0} W."
            : $"The GPU draws {power:0} W while idle{clockText}.{why}");
    }

    /// <summary>How many monitors the main GPU drives, from the display devices attached to the desktop.</summary>
    private static int MonitorsOn(string? gpuName)
    {
        if (gpuName is null) return 0;
        int count = 0;
        var device = new NativeMethods.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & 1) != 0 /* attached to the desktop */ && device.DeviceString.Contains(gpuName, StringComparison.OrdinalIgnoreCase))
                count++;
            device.cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
        }
        return count;
    }

    [GeneratedRegex(@"(?<!\d)(\d{4})(?!\d)")]
    private static partial Regex FourDigitSpeed();

    [GeneratedRegex(@"^KF\d?(\d{2})C", RegexOptions.IgnoreCase)]
    private static partial Regex KingstonSpeed();
}
