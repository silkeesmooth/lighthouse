using System.Runtime.InteropServices;
using System.Text;

namespace Lighthouse.Core.Native;

internal static class NativeMethods
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    public const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    /// <summary>
    /// A folder opened for listing, without following a link and without letting anyone else delete or rename it
    /// while the handle is open (no FILE_SHARE_DELETE).
    /// </summary>
    public static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path) =>
        CreateFileW(path, 0x0001 /* FILE_LIST_DIRECTORY */ | 0x0080 /* FILE_READ_ATTRIBUTES */, 0x1 | 0x2 /* share read, write */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 /* BACKUP_SEMANTICS */ | 0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    /// <summary>The interface index Windows would send traffic for <paramref name="destAddr"/> (IPv4, network byte order) through.</summary>
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupAccountSid(string? system, IntPtr sid, StringBuilder name, ref int nameLen,
        StringBuilder domain, ref int domainLen, out int use);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    /// <summary>Splits a command line exactly the way Windows programs see their argv.</summary>
    public static string[] SplitArgs(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var args = new string[argc];
            for (int i = 0; i < argc; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
