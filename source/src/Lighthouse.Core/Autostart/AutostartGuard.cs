using Microsoft.Win32;

namespace Lighthouse.Core.Autostart;

/// <summary>
/// A startup entry to keep off, from settings.json "autostartOff". Kind "run" = a Run-key value (Hive HKLM/HKCU),
/// "task" = a scheduled task (Path is its folder), "toast" = an app's Windows notifications, "cmdpal" = a PowerToys
/// Command Palette extension (tools\autostart.ps1 only). <see cref="Why"/> is a note for the list.
/// </summary>
public sealed record AutostartEntry(string Kind, string Name, string? Hive = null, string? Path = null, string? Why = null)
{
    public override string ToString() => Kind switch
    {
        "task" => $"task {Path}{Name}",
        "run" => $"{Hive} Run \"{Name}\"",
        _ => $"{Name} notifications",
    };
}

public interface IAutostartBackend
{
    /// <summary>True if enabled, false if disabled, null if the entry does not exist.</summary>
    bool? IsEnabled(AutostartEntry entry);
    void Disable(AutostartEntry entry);
}

/// <summary>
/// Keeps the startup entries listed in settings.json "autostartOff" switched off: driver and app updates like to
/// put their startup tasks back. Only the listed entries, nothing else, and the list is empty until you fill it.
/// tools\autostart.ps1 -Restore creates the "off" marker to stop it; -Disable deletes the marker.
/// </summary>
public sealed class AutostartGuard(string offMarker, IAutostartBackend backend)
{
    public static List<AutostartEntry> NeedsDisabling(IEnumerable<AutostartEntry> entries, Func<AutostartEntry, bool?> isEnabled) =>
        entries.Where(e => isEnabled(e) == true).ToList();

    /// <summary>Switches off anything on the list that is on. Returns what it switched off.</summary>
    public List<AutostartEntry> Enforce(IReadOnlyList<AutostartEntry> entries)
    {
        var turnedOff = new List<AutostartEntry>();
        if (File.Exists(offMarker) || entries.Count == 0) return turnedOff;
        foreach (var entry in NeedsDisabling(entries, backend.IsEnabled))
        {
            try
            {
                backend.Disable(entry);
                if (backend.IsEnabled(entry) == false) turnedOff.Add(entry);
            }
            catch
            {
                // Needs admin; the next run as admin will catch it.
            }
        }
        return turnedOff;
    }
}

/// <summary>The real registry and Task Scheduler.</summary>
public sealed class WindowsAutostartBackend : IAutostartBackend
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";

    public bool? IsEnabled(AutostartEntry e) => e.Kind switch
    {
        "run" => RunEnabled(e),
        "task" => ScheduledTasks.IsEnabled(e.Path ?? "\\", e.Name),
        "toast" => ToastEnabled(e),
        _ => null,
    };

    public void Disable(AutostartEntry e)
    {
        switch (e.Kind)
        {
            case "run":
                using (var approved = Root(e.Hive).CreateSubKey(ApprovedKey, true))
                {
                    // StartupApproved: first byte 03 = disabled (Task Manager's format), then the FILETIME it happened.
                    var value = new byte[12];
                    value[0] = 3;
                    BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
                    approved.SetValue(e.Name, value, RegistryValueKind.Binary);
                }
                break;
            case "task":
                ScheduledTasks.SetEnabled(e.Path ?? "\\", e.Name, false);
                break;
            case "toast":
                using (var key = Registry.CurrentUser.CreateSubKey(ToastKey + e.Name, true))
                    key.SetValue("Enabled", 0, RegistryValueKind.DWord);
                break;
        }
    }

    private static RegistryKey Root(string? hive) =>
        string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;

    private static bool? RunEnabled(AutostartEntry e)
    {
        using var run = Root(e.Hive).OpenSubKey(RunKey);
        if (run?.GetValue(e.Name) is null) return null;
        using var approved = Root(e.Hive).OpenSubKey(ApprovedKey);
        // No StartupApproved value means enabled; an odd first byte means disabled.
        return approved?.GetValue(e.Name) is not byte[] { Length: > 0 } bytes || (bytes[0] & 1) == 0;
    }

    private static bool? ToastEnabled(AutostartEntry e)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ToastKey + e.Name);
        return key?.GetValue("Enabled") is not int v || v != 0;
    }
}

/// <summary>Task Scheduler through its COM API (no schtasks parsing).</summary>
public static class ScheduledTasks
{
    public static bool? IsEnabled(string folder, string name)
    {
        try
        {
            dynamic? task = Find(folder, name);
            return task is null ? null : (bool)task.Enabled;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Starts a task now, as it is configured (an elevated task runs elevated; the user needs only run rights).</summary>
    public static void Run(string folder, string name)
    {
        dynamic task = Find(folder, name) ?? throw new InvalidOperationException($"No task {folder}{name}");
        task.Run(null);
    }

    public static void SetEnabled(string folder, string name, bool enabled)
    {
        dynamic task = Find(folder, name) ?? throw new InvalidOperationException($"No task {folder}{name}");
        task.Enabled = enabled;
    }

    /// <summary>
    /// A folder path as Task Scheduler's GetFolder takes it: "\" for the root, else no trailing backslash
    /// ("\Lighthouse\" is refused with 0x8007007B, "\Lighthouse" works).
    /// </summary>
    public static string FolderPath(string folder)
    {
        string trimmed = folder.Trim().TrimEnd('\\');
        return trimmed.Length == 0 ? "\\" : trimmed.StartsWith('\\') ? trimmed : "\\" + trimmed;
    }

    private static dynamic? Find(string folder, string name)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        try
        {
            dynamic f = service.GetFolder(FolderPath(folder));
            return f.GetTask(name);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
