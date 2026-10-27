using System.Runtime.InteropServices;

namespace Lighthouse.Core.Sensors;

/// <summary>Thin wrapper over the Performance Data Helper API (the counters Task Manager reads).</summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;

    public PdhQuery()
    {
        uint status = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (status != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X8}");
    }

    /// <summary>Adds an English counter path. Returns null when this machine lacks the counter.</summary>
    public PdhCounter? Add(string path)
    {
        uint status = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr counter);
        return status == 0 ? new PdhCounter(counter) : null;
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed class PdhCounter
{
    private readonly IntPtr _counter;

    internal PdhCounter(IntPtr counter) => _counter = counter;

    public double? Value(bool cap100 = true)
    {
        uint format = PdhQuery.PDH_FMT_DOUBLE | (cap100 ? 0 : PdhQuery.PDH_FMT_NOCAP100);
        uint status = PdhGetFormattedCounterValue(_counter, format, out _, out FmtValue value);
        return status == 0 && value.CStatus <= 1 ? value.Double : null;
    }

    /// <summary>Every instance of a wildcard counter. Instances that come and go are re-read each collect.</summary>
    public List<(string Instance, double Value)> Array()
    {
        var result = new List<(string, double)>();
        uint format = PdhQuery.PDH_FMT_DOUBLE | PdhQuery.PDH_FMT_NOCAP100;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            uint status = PdhGetFormattedCounterArray(_counter, format, ref size, out _, IntPtr.Zero);
            if (status != PdhQuery.PDH_MORE_DATA) return result;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, format, ref size, out uint count, buffer);
                if (status == PdhQuery.PDH_MORE_DATA) continue;
                if (status != 0) return result;
                // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then {CStatus, pad, double}.
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * 24;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                    uint cstatus = (uint)Marshal.ReadInt32(item, 8);
                    if (cstatus > 1) continue;
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    result.Add((name, value));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FmtValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr itemBuffer);
}
