using System.Runtime.InteropServices;

namespace Lighthouse.Core.Sensors;

public sealed record GpuAdapter(string Name, double VramGb, uint LuidLow, int LuidHigh)
{
    /// <summary>The LUID as it appears in PDH instance names: "luid_0x00000000_0x1fc2866b".</summary>
    public string PdhLuid => $"luid_0x{LuidHigh:x8}_0x{LuidLow:x8}";
}

/// <summary>Finds the discrete GPU (the adapter with the most dedicated memory) and its exact VRAM size.</summary>
public static class Dxgi
{
    public static GpuAdapter? MainAdapter()
    {
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out object obj) != 0) return null;
            var factory = (IDXGIFactory1)obj;
            GpuAdapter? best = null;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter) == 0; i++)
            {
                if (adapter.GetDesc1(out AdapterDesc1 desc) == 0 && (desc.Flags & 2) == 0) // 2 = software adapter
                {
                    double gb = desc.DedicatedVideoMemory / (1024.0 * 1024 * 1024);
                    if (best is null || gb > best.VramGb)
                        best = new GpuAdapter(desc.Description.Trim(), gb, desc.LuidLow, desc.LuidHigh);
                }
                Marshal.ReleaseComObject(adapter);
            }
            Marshal.ReleaseComObject(factory);
            return best;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Only the vtable order matters for the slots we never call.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }
}
