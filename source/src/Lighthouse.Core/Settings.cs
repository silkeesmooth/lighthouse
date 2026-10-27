using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lighthouse.Core.Alerts;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Core;

public static class Paths
{
    /// <summary>%LOCALAPPDATA%\Lighthouse. Tests point it at a temp folder so they never write to the real log.</summary>
    public static string DataDir { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lighthouse");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryDb => Path.Combine(DataDir, "history.db");
    public static string LogFile => Path.Combine(DataDir, "lighthouse.log");
    /// <summary>tools\autostart.ps1 -Restore creates this to stop the autostart guard; -Disable deletes it.</summary>
    public static string GuardOffFile => Path.Combine(DataDir, "autostart-guard-off");

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? _dataDirLock;

    /// <summary>
    /// For the running copy, which is elevated: refuses a data folder that is a link (junction, mount point or
    /// symbolic link), then holds it open, without letting it be deleted or renamed, so it can't be swapped for one
    /// while Lighthouse writes into it. A link there would send an admin's writes wherever it points. Returns why it
    /// can't be used, or null.
    /// </summary>
    public static string? LockDataDir()
    {
        Directory.CreateDirectory(DataDir);
        if (IsLink(DataDir)) return $"{DataDir} is a link to another folder";
        var handle = Native.NativeMethods.OpenDirectory(DataDir);
        if (handle.IsInvalid) return $"{DataDir} could not be opened (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})";
        if (IsLink(DataDir)) // swapped between the check and the open
        {
            handle.Dispose();
            return $"{DataDir} is a link to another folder";
        }
        _dataDirLock = handle;
        return null;
    }

    /// <summary>True for a junction, mount point or symbolic link (or anything whose attributes can't be read).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // nothing there yet
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>Everything Lighthouse remembers between runs. Edit settings.json and reopen the flyout to apply.</summary>
public sealed record Settings
{
    public Thresholds Thresholds { get; init; } = new();
    public List<WorkloadOverride> WorkloadNames { get; init; } = [];
    /// <summary>Scripts and pipelines to watch: each run gets a row, a boat and a toast when it ends (see examples\jobs.example.json).</summary>
    public List<JobRule> Jobs { get; init; } = [];
    public DateTime? AlertsPausedUntil { get; set; }
    /// <summary>The small always-on-top window; off by default.</summary>
    public bool MiniMonitor { get; set; }
    /// <summary>
    /// Where the mini opens and goes back to: a corner or edge middle of the main screen ("bottom-right", "bottom",
    /// "bottom-left", "left", "top-left", "top", "top-right", "right"), or "custom" for <see cref="MiniLeft"/> and
    /// <see cref="MiniTop"/>. Set from the mini's Favorite spot menu; a drag only moves it for now.
    /// </summary>
    public string MiniFavorite { get; set; } = "bottom-right";
    /// <summary>"system" follows Windows' app theme; "light" or "dark" is the flyout's Dark mode switch.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>The key under the scene, opened with its "?".</summary>
    public bool SceneLegend { get; set; }
    /// <summary>Cards closed with their ×, kept closed until Windows restarts (<see cref="ClosedCardsBoot"/>).</summary>
    public List<string> ClosedCards { get; set; } = [];
    public DateTime? ClosedCardsBoot { get; set; }
    /// <summary>LM Studio's server port, when it isn't LM Studio's default (1234).</summary>
    public int? LmStudioPort { get; init; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    /// <summary>
    /// How solid the mini monitor's background panel is (1 = solid), mouse or not. Only the panel: the scene and
    /// gauges always draw solid.
    /// </summary>
    public double MiniBackgroundOpacity { get; set; } = 0.35;
    /// <summary>The mini's layout: "full" (lighthouse over gauges), "strip" (side by side), "lighthouse", "numbers".</summary>
    public string MiniLayout { get; set; } = "full";
    /// <summary>The mini slides off text under it, and back to its favorite spot once that is clear.</summary>
    public bool MiniAvoidText { get; set; } = true;
    /// <summary>
    /// While a fullscreen video or game covers the mini's screen: "move" to another screen (or hide when there is
    /// none), "hide", or "stay" on top. It comes back when fullscreen ends.
    /// </summary>
    public string MiniFullscreen { get; set; } = "move";
    /// <summary>Clicks go through the mini to what is behind it; switched from the tray menu.</summary>
    public bool MiniClickThrough { get; set; }
    public bool QuietInGames { get; init; } = true;
    /// <summary>"Using it now" shows "load" (CPU, GPU, RAM, VRAM) or "io" (CPU, disk, network down and up).</summary>
    public string UsingView { get; set; } = "load";
    /// <summary>Job runs shorter than this finish without a toast (they are still logged).</summary>
    public double JobToastMinSeconds { get; init; } = 60;
    public double GameSummaryMinMinutes { get; init; } = 5;
    /// <summary>The exe whose tray icon Lighthouse already moved out of the overflow (once, so a later "hide" sticks).</summary>
    public string? PromotedExe { get; set; }
    /// <summary>Startup entries the autostart guard keeps off (see examples\settings.example.json). Empty: it does nothing.</summary>
    public List<Autostart.AutostartEntry> AutostartOff { get; init; } = [];
    /// <summary>The folder the local LLM's start command changes to first. Default: the server's own folder.</summary>
    public string? LlmStartDirectory { get; init; }
    /// <summary>The PowerShell line that restarts the local LLM, kept while it is paused for a game.</summary>
    public string? PausedLlmStartCommand { get; set; }
    public Dictionary<string, bool> LastHealth { get; set; } = [];
    /// <summary>Health checks the user dismissed: no card, no toast.</summary>
    public List<string> DismissedHealth { get; set; } = [];
    public DateTime? LastDriverResetCheck { get; set; }
    /// <summary>Your internet plan, so a speed test says how much of it you got. Null: not set.</summary>
    public InternetPlan? InternetPlan { get; init; }
    /// <summary>A daily speed test at this time ("03:30"), run only when you've been away 10 minutes and no game runs. Null: off.</summary>
    public string? DailySpeedTest { get; init; }
    public DateTime? LastDailySpeedTest { get; set; }
    /// <summary>When the last weekly digest was made (Mondays from 06:00).</summary>
    public DateTime? LastDigest { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), JsonOptions) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Write($"settings.json unreadable, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        string tmp = Paths.SettingsFile + ".tmp";
        // A fresh file every time: deleting a planted link removes the link, and CreateNew never follows one.
        if (File.Exists(tmp) || Paths.IsLink(tmp)) File.Delete(tmp);
        using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream))
            writer.Write(JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, Paths.SettingsFile, overwrite: true); // replaces the name, never writes through a link
    }

    /// <summary>A value as settings.json would write it, for comparing lists that records compare by reference.</summary>
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// These settings with every setting <paramref name="json"/> names copied over them (`Lighthouse.exe --import`).
    /// A named setting replaces the whole value, lists included; settings it doesn't name are kept. Names Lighthouse
    /// doesn't know come back in Unknown rather than vanishing silently.
    /// </summary>
    public (Settings Merged, IReadOnlyList<string> Imported, IReadOnlyList<string> Unknown) Merge(string json)
    {
        var known = typeof(Settings).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = JsonNode.Parse(JsonSerializer.Serialize(this, JsonOptions))!.AsObject();
        var source = JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException("The file is not a JSON object.");
        List<string> imported = [], unknown = [];
        foreach (var (name, value) in source.ToList())
        {
            if (!known.Contains(name))
            {
                unknown.Add(name);
                continue;
            }
            foreach (string same in target.Select(kv => kv.Key).Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
                target.Remove(same);
            target[JsonNamingPolicy.CamelCase.ConvertName(name)] = value?.DeepClone();
            imported.Add(name);
        }
        return (target.Deserialize<Settings>(JsonOptions) ?? new Settings(), imported, unknown);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // settings.json is edited by hand: keep regular expressions readable ("\\s+", not "\\s\u002B").
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Why the last write failed, shown in the flyout so a silent logging failure is still visible.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Set by the sensor helper, which writes no files: its lines go to the app, which logs them.</summary>
    public static Action<string>? Redirect { get; set; }

    public static void Write(string message)
    {
        if (Redirect is { } redirect)
        {
            redirect(message);
            return;
        }
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                if (Paths.IsLink(Paths.LogFile)) throw new IOException("the log file is a link, so nothing is written through it");
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1_000_000) File.Move(Paths.LogFile, Paths.LogFile + ".old", overwrite: true);
                File.AppendAllText(Paths.LogFile, line);
                LastError = null;
            }
            catch (Exception e)
            {
                // Logging must never take the monitor down, but the failure must not vanish either.
                LastError = $"{e.GetType().Name}: {e.Message}";
                try
                {
                    File.AppendAllText(Path.Combine(Paths.DataDir, $"lighthouse-{Environment.ProcessId}.log"),
                        $"{line}  (main log failed: {LastError}){Environment.NewLine}");
                }
                catch
                {
                    // Nowhere left to write.
                }
            }
        }
    }
}
