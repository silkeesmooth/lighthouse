using System.Runtime.InteropServices;

namespace Lighthouse.Core.Sensors;

/// <summary>A graphics card as Device Manager lists it. <see cref="Problem"/> is its error code (31, 43, ...), 0 when it works.</summary>
public sealed record DisplayAdapter(string Name, int Problem)
{
    /// <summary>Code 22: someone switched it off in Device Manager. That is a choice, not a fault.</summary>
    public const int DisabledByUser = 22;

    public bool Failed => Problem != 0 && Problem != DisabledByUser;
}

/// <summary>
/// Spots a graphics driver that has stopped. When the AMD or NVIDIA driver crashes and Windows can't reload it, the card
/// drops out of the GPU counters and the driver's sensors: without this, the GPU and VRAM gauges just read 0.
/// </summary>
public static class DisplayAdapters
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint FilterClass = 0x200, FilterPresent = 0x100, HasProblem = 0x400;
    private const uint DeviceDesc = 0x01, FriendlyName = 0x0D;
    private const double BuiltInMaxGb = 4;

    /// <summary>The display adapters that are plugged in, with their Device Manager problem codes.</summary>
    public static IReadOnlyList<DisplayAdapter> List()
    {
        var result = new List<DisplayAdapter>();
        try
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, DisplayClass, FilterClass | FilterPresent) != 0 || length == 0) return result;
            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW(DisplayClass, buffer, length, FilterClass | FilterPresent) != 0) return result;
            foreach (string id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) continue;
                int problem = CM_Get_DevNode_Status(out uint status, out uint code, node, 0) == 0 && (status & HasProblem) != 0 ? (int)code : 0;
                string? name = Property(node, FriendlyName) ?? Property(node, DeviceDesc);
                if (name is not null) result.Add(new DisplayAdapter(name, problem));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return result;
    }

    /// <summary>
    /// The card whose driver stopped, if the GPU gauges depend on it: the one Lighthouse reads (by name), or any failed
    /// card when what Lighthouse reads is the CPU's built-in graphics or nothing at all, because a dead graphics card is
    /// missing from the list Lighthouse picks its GPU from. Built-in graphics is told apart by its memory: the BIOS
    /// gives it a slice of RAM, 4 GB at most on the boards this has met (a Ryzen 7950X3D's reads 2.0 GB). A failed
    /// built-in GPU next to a working graphics card doesn't count.
    /// </summary>
    public static DisplayAdapter? Stopped(IReadOnlyList<DisplayAdapter> adapters, GpuAdapter? main)
    {
        var failed = adapters.Where(a => a.Failed).ToList();
        if (failed.Count == 0) return null;
        if (main is null) return failed[0];
        return failed.FirstOrDefault(a => SameName(a.Name, main.Name))
               ?? (main.VramGb <= BuiltInMaxGb ?failed.FirstOrDefault(a => !SameName(a.Name, main.Name)) : null);
    }

    private static bool SameName(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    private static string? Property(uint node, uint property)
    {
        uint bytes = 0;
        CM_Get_DevNode_Registry_PropertyW(node, property, out _, null, ref bytes, 0);
        if (bytes < 4) return null;
        var buffer = new char[bytes / 2];
        if (CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref bytes, 0) != 0) return null;
        string text = new string(buffer).TrimEnd('\0').Trim();
        // An unlocalized description reads "@oem58.inf,%amd744c.1%;AMD Radeon RX 7900 XT": the name is after the ';'.
        int semicolon = text.LastIndexOf(';');
        if (text.StartsWith('@') && semicolon >= 0) text = text[(semicolon + 1)..].Trim();
        return text.Length > 0 ? text : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] char[]? buffer,
        ref uint length, uint flags);
}
