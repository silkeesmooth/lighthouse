using System.Runtime.InteropServices;

namespace Lighthouse.Core.Native;

/// <summary>
/// Paths Windows writes with a known folder in place of its start, such as the tray's
/// "{6D809377-6AF0-444B-8957-A3773F02200E}\Lighthouse\Lighthouse.exe" for C:\Program Files\Lighthouse\Lighthouse.exe.
/// </summary>
public static class KnownFolders
{
    /// <summary>The path with a leading "{folder id}" replaced by that folder; unchanged when it has none or it is unknown.</summary>
    public static string Expand(string path, Func<Guid, string?>? resolve = null)
    {
        if (!path.StartsWith('{')) return path;
        int close = path.IndexOf('}');
        if (close < 0 || !Guid.TryParse(path[1..close], out var id)) return path;
        return (resolve ?? Resolve)(id) is { Length: > 0 } folder ? folder.TrimEnd('\\') + path[(close + 1)..] : path;
    }

    /// <summary>The same file, however Windows wrote it.</summary>
    public static bool SamePath(string stored, string path, Func<Guid, string?>? resolve = null) =>
        string.Equals(Expand(stored, resolve), path, StringComparison.OrdinalIgnoreCase);

    private static string? Resolve(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr buffer) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
