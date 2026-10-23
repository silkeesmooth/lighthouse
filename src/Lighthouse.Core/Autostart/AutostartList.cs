using System.Text.Json;
using Microsoft.Win32;

namespace Lighthouse.Core.Autostart;

/// <summary>
/// The startup entries the guard keeps off, as an administrator saved them (tools\autostart.ps1 -Disable writes
/// HKLM\SOFTWARE\Lighthouse "AutostartOff"). settings.json's "autostartOff" is only the request: any program you run
/// can edit that file, and the guard runs as admin, so it never takes its list from there.
/// </summary>
public static class AutostartList
{
    public const string KeyPath = @"SOFTWARE\Lighthouse";
    public const string ValueName = "AutostartOff";

    public static IReadOnlyList<AutostartEntry> Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string json ? Parse(json) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>The entries in a saved list that the guard may touch (see <see cref="Allowed"/>); anything unreadable is none.</summary>
    public static List<AutostartEntry> Parse(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<AutostartEntry>>(json, Settings.JsonOptions) ?? []).Where(Allowed).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Windows Security's own Run values.</summary>
    private static readonly HashSet<string> WindowsRunValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth", "WindowsDefender", "Windows Defender",
    };

    /// <summary>
    /// Third-party startup entries only: a Run value, a scheduled task outside Windows' own \Microsoft\ folder, or an
    /// app's notifications. Windows Defender, Windows Update and the rest of Windows are never switched off.
    /// </summary>
    public static bool Allowed(AutostartEntry e) => e.Kind switch
    {
        "run" => (e.Hive is null || e.Hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || e.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
                 && !WindowsRunValues.Contains(e.Name),
        "task" => !(e.Path ?? "\\").TrimStart('\\').StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase),
        "toast" => !e.Name.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
