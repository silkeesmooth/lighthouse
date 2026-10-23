using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lighthouse.Core.Native;

/// <summary>
/// The signed-in user's own rights, for an app started as admin by hand. The app normally runs with them already
/// (only the sensor helper is admin), and then this does its work directly. Started as admin, it must not use admin
/// rights on behalf of other programs: reading a file another program named (the local LLM's key file comes from its
/// command line) or starting an app (an elevated Notepad can open any file). So it borrows the desktop shell's
/// token, which is the same user without elevation, and does those with it.
/// </summary>
public static class UserContext
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004, TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2, TokenPrimary = 1, TokenImpersonation = 2;

    public static bool Elevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Runs <paramref name="work"/> with the user's own rights (directly, when Lighthouse isn't elevated).</summary>
    public static T Run<T>(Func<T> work)
    {
        if (!Elevated) return work();
        using var token = ShellToken(TokenImpersonation)
            ?? throw new UnauthorizedAccessException("There is no desktop shell to take the user's own rights from.");
        return WindowsIdentity.RunImpersonated(token, work);
    }

    /// <summary>Starts a program with the user's own rights, never elevated.</summary>
    public static void Start(string exe, string arguments)
    {
        if (!Elevated)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = false })?.Dispose();
            return;
        }
        using var token = ShellToken(TokenPrimary)
            ?? throw new UnauthorizedAccessException("There is no desktop shell to take the user's own rights from.");
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var commandLine = new StringBuilder($"\"{exe}\" {arguments}");
        if (!CreateProcessWithTokenW(token, 0, exe, commandLine, 0, IntPtr.Zero, null, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        NativeMethods.CloseHandle(info.hThread);
        NativeMethods.CloseHandle(info.hProcess);
    }

    /// <summary>A line for the log at startup: whether the user's own rights are available.</summary>
    public static string Describe()
    {
        if (!Elevated) return "not elevated";
        try
        {
            string who = Run(() => WindowsIdentity.GetCurrent().Name + (new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator) ? " (still admin!)" : " without admin rights"));
            return $"elevated; other programs' files are read as {who}";
        }
        catch (Exception e)
        {
            return $"elevated; the user's own rights are unavailable ({e.Message}), so files other programs name are not read";
        }
    }

    /// <summary>The desktop shell's token, if the shell runs as this same user.</summary>
    private static SafeAccessTokenHandle? ShellToken(int type)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out uint pid) == 0) return null;
        IntPtr process = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TOKEN_DUPLICATE | TOKEN_QUERY, out var shellToken)) return null;
            using (shellToken)
            {
                using (var shellUser = new WindowsIdentity(shellToken.DangerousGetHandle()))
                using (var me = WindowsIdentity.GetCurrent())
                    if (shellUser.User != me.User) return null; // someone else's shell: borrow nothing
                uint access = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
                return DuplicateTokenEx(shellToken, access, IntPtr.Zero, SecurityImpersonation, type, out var copy) ? copy : null;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int level, int type,
        out SafeAccessTokenHandle copy);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string? applicationName,
        StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);
}
