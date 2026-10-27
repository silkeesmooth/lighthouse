using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Sensors;

/// <summary>
/// One process as the kernel reports it. CpuTime is user + kernel, in 100 ns units. IoBytes is everything it has read
/// and written so far (files, devices, pipes), the count Task Manager's I/O columns show.
/// </summary>
public sealed record ProcInfo(int Pid, int ParentPid, string Name, long CreateTime, long CpuTime, long PrivateWorkingSet,
    long WorkingSet, long IoBytes = 0);

/// <summary>
/// Reads every process in one NtQuerySystemInformation call: cheaper than Process.GetProcesses
/// and it works for protected processes too (their times and memory are still reported).
/// </summary>
public static class ProcessTable
{
    private const int SystemProcessInformation = 5;

    public static List<ProcInfo> Read()
    {
        int size = 2 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(needed, size * 2) + (256 << 10);
                    continue;
                }
                if (status != 0) throw new Win32Exception($"NtQuerySystemInformation failed: 0x{status:X8}");
                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // SYSTEM_PROCESS_INFORMATION offsets on x64.
    private static List<ProcInfo> Parse(IntPtr buffer)
    {
        var list = new List<ProcInfo>(512);
        int offset = 0;
        while (true)
        {
            IntPtr entry = buffer + offset;
            int next = Marshal.ReadInt32(entry, 0);
            long privateWs = Marshal.ReadInt64(entry, 8);
            long createTime = Marshal.ReadInt64(entry, 32);
            long userTime = Marshal.ReadInt64(entry, 40);
            long kernelTime = Marshal.ReadInt64(entry, 48);
            ushort nameBytes = (ushort)Marshal.ReadInt16(entry, 56);
            IntPtr namePtr = Marshal.ReadIntPtr(entry, 64);
            int pid = (int)Marshal.ReadIntPtr(entry, 80);
            int parent = (int)Marshal.ReadIntPtr(entry, 88);
            long workingSet = (long)Marshal.ReadIntPtr(entry, 144);
            long ioBytes = Marshal.ReadInt64(entry, 232) + Marshal.ReadInt64(entry, 240); // ReadTransferCount + WriteTransferCount
            string name = pid == 0 ? "Idle" : namePtr == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(namePtr, nameBytes / 2);
            list.Add(new ProcInfo(pid, parent, name, createTime, userTime + kernelTime, privateWs, workingSet, ioBytes));
            if (next == 0) break;
            offset += next;
        }
        return list;
    }

    /// <summary>Full image path and command line. Both are null when the process cannot be opened.</summary>
    public static (string? Path, string? CommandLine) Details(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            string? path = NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref len) ? sb.ToString() : null;
            return (path, CommandLine(handle));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The folder a process is working in, read from its own memory (PEB, then its process parameters' current
    /// directory). Only for the user's own processes; null when it can't be read. Used to name Claude Code sessions.
    /// </summary>
    public static string? CurrentDirectory(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;
        IntPtr basic = Marshal.AllocHGlobal(48);
        try
        {
            if (NativeMethods.NtQueryInformationProcess(handle, 0 /* ProcessBasicInformation */, basic, 48, out _) != 0) return null;
            IntPtr peb = Marshal.ReadIntPtr(basic, 8);
            if (Read(handle, peb + 0x20, 8) is not { } parametersPtr) return null;                  // PEB.ProcessParameters
            IntPtr parameters = (IntPtr)BitConverter.ToInt64(parametersPtr, 0);
            if (Read(handle, parameters + 0x38, 16) is not { } dosPath) return null;                // CurrentDirectory.DosPath
            int length = BitConverter.ToUInt16(dosPath, 0);
            if (length is 0 or > 4096) return null;
            return Read(handle, (IntPtr)BitConverter.ToInt64(dosPath, 8), length) is { } text ? Encoding.Unicode.GetString(text) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(basic);
            NativeMethods.CloseHandle(handle);
        }
    }

    private static byte[]? Read(IntPtr process, IntPtr address, int count)
    {
        var buffer = new byte[count];
        return NativeMethods.ReadProcessMemory(process, address, buffer, count, out IntPtr read) && read == count ? buffer : null;
    }

    private const int ProcessCommandLineInformation = 60;

    private static string? CommandLine(IntPtr handle)
    {
        int size = 4096;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NativeMethods.NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out int needed);
                if (status is NativeMethods.STATUS_INFO_LENGTH_MISMATCH or NativeMethods.STATUS_BUFFER_OVERFLOW
                    or NativeMethods.STATUS_BUFFER_TOO_SMALL)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }
                if (status != 0) return null;
                // The buffer starts with a UNICODE_STRING that points into itself.
                ushort length = (ushort)Marshal.ReadInt16(buffer, 0);
                IntPtr text = Marshal.ReadIntPtr(buffer, 8);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>"DOMAIN\user" that owns the process, or null.</summary>
    public static string? Owner(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!NativeMethods.OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return null;
            try
            {
                NativeMethods.GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out int needed);
                IntPtr info = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, 1, info, needed, out _)) return null;
                    IntPtr sid = Marshal.ReadIntPtr(info, 0);
                    var name = new StringBuilder(256);
                    var domain = new StringBuilder(256);
                    int nameLen = name.Capacity, domainLen = domain.Capacity;
                    return NativeMethods.LookupAccountSid(null, sid, name, ref nameLen, domain, ref domainLen, out _)
                        ? $"{domain}\\{name}"
                        : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(info);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
