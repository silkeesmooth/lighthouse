using System.Runtime.InteropServices;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Sensors;

/// <summary>One CCD: the logical processors that share an L3 cache.</summary>
public sealed record Ccd(int Index, long L3Bytes, IReadOnlyList<int> Processors);

/// <summary>
/// CCD layout from the L3 caches Windows reports. On a dual-CCD X3D chip (7950X3D, 9950X3D) one CCD has 96 MB of L3 (the V-Cache die)
/// and the other 32 MB, so the bigger cache marks the V-Cache CCD without hard-coding core numbers.
/// </summary>
public sealed record CpuTopology(IReadOnlyList<Ccd> Ccds)
{
    public Ccd? CacheCcd => Ccds.Count > 1 && Ccds.Select(c => c.L3Bytes).Distinct().Count() > 1
        ? Ccds.MaxBy(c => c.L3Bytes)
        : null;

    public Ccd? OtherCcd => CacheCcd is { } cache ? Ccds.FirstOrDefault(c => c != cache) : null;

    public static CpuTopology Read()
    {
        const int RelationCache = 2;
        uint length = 0;
        NativeMethods.GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
        if (length == 0) return new CpuTopology([]);
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(RelationCache, buffer, ref length))
                return new CpuTopology([]);
            var l3 = new List<(long Size, ulong Mask, int Group)>();
            int offset = 0;
            while (offset < length)
            {
                IntPtr record = buffer + offset;
                int size = Marshal.ReadInt32(record, 4);
                byte level = Marshal.ReadByte(record, 8);
                long cacheSize = (uint)Marshal.ReadInt32(record, 12);
                ulong mask = (ulong)Marshal.ReadInt64(record, 40);
                int group = Marshal.ReadInt16(record, 48);
                if (level == 3) l3.Add((cacheSize, mask, group));
                offset += size;
            }
            return FromL3(l3);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static CpuTopology FromL3(IEnumerable<(long Size, ulong Mask, int Group)> l3)
    {
        var ccds = l3
            .Select(c => (c.Size, Processors: Enumerable.Range(0, 64).Where(b => (c.Mask & (1UL << b)) != 0)
                .Select(b => c.Group * 64 + b).ToList()))
            .OrderBy(c => c.Processors.FirstOrDefault())
            .Select((c, i) => new Ccd(i, c.Size, c.Processors))
            .ToList();
        return new CpuTopology(ccds);
    }

    /// <summary>Which CCD is parked, given each logical processor's PDH "Parking Status" (true = parked).</summary>
    public CcdState StateFrom(IReadOnlyDictionary<int, bool> parked)
    {
        if (CacheCcd is not { } cache || OtherCcd is not { } other || parked.Count == 0) return CcdState.Unknown;
        bool AllParked(Ccd c) => c.Processors.All(p => parked.TryGetValue(p, out bool v) && v);
        bool NoneParked(Ccd c) => c.Processors.All(p => !parked.TryGetValue(p, out bool v) || !v);
        if (AllParked(other) && NoneParked(cache)) return CcdState.OtherParked;
        if (AllParked(cache) && NoneParked(other)) return CcdState.CacheParked;
        if (NoneParked(cache) && NoneParked(other)) return CcdState.BothActive;
        return CcdState.Partial;
    }
}
