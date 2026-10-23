using Lighthouse.Core.Model;

namespace Lighthouse.Core.Workloads;

/// <summary>What Lighthouse knows about a process before it is grouped.</summary>
public sealed record ProcessFacts(int Pid, int ParentPid, string Name, string? Path, string? CommandLine, long CreateTime,
    string? Owner = null, string? Folder = null);

/// <summary>What a Claude Code session is working on, from its working folder.</summary>
public static class ClaudeCodeProject
{
    /// <summary>
    /// "lighthouse" from C:\Users\you\Documents\lighthouse; a worktree (…\lighthouse\.claude\worktrees\x) is its
    /// repository's name; the home folder is "home"; a drive root or nothing is null.
    /// </summary>
    public static string? Name(string? folder, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string trimmed = folder.TrimEnd('\\', '/');
        if (home is not null && string.Equals(trimmed, home.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return "home";
        var parts = trimmed.Split('\\', '/').Where(p => p.Length > 0).ToList();
        int dotClaude = parts.FindLastIndex(p => p.Equals(".claude", StringComparison.OrdinalIgnoreCase));
        if (dotClaude > 0 && dotClaude + 1 < parts.Count && parts[dotClaude + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase))
            return parts[dotClaude - 1];
        return parts.Count <= 1 ? null : parts[^1]; // "C:" alone is a drive root
    }
}

/// <summary>
/// A process's own workload. <see cref="Generic"/> means "no rule named it": such a process joins the nearest
/// ancestor that is an app (a helper under Chrome counts as Chrome). <see cref="Host"/> processes only host
/// someone else's work and always join their direct parent (conhost under a game, a WebView2 under Teams).
/// </summary>
public sealed record Classification(string Key, string Label, WorkloadKind Kind, bool Generic = false, bool Host = false,
    JobRule? Job = null);

/// <summary>A user-defined name: a process whose name or path contains <see cref="Match"/> gets <see cref="Label"/>.</summary>
public sealed record WorkloadOverride(string Match, string Label);

/// <summary>Names processes the way a person thinks about them: AI tools, their own jobs, games, apps, Windows.</summary>
public sealed class Classifier(int ownPid, IReadOnlyList<WorkloadOverride>? overrides = null, IReadOnlyList<JobRule>? jobs = null)
{
    private readonly IReadOnlyList<WorkloadOverride> _overrides = overrides ?? [];
    private readonly JobMatcher _jobs = new(jobs);

    public Classification Classify(ProcessFacts p)
    {
        string name = p.Name;
        string lower = name.ToLowerInvariant();
        string path = p.Path ?? "";
        string cmd = p.CommandLine ?? "";

        if (p.Pid == ownPid) return new("lighthouse", "Lighthouse", WorkloadKind.Lighthouse);

        // Only the server is "the local LLM" (the moon, the VRAM warning, Explain), even under a name override that
        // covers its folder: an installer downloading a model there is a tool working on it, not the server.
        switch (LocalLlmApps.KindOf(name, p.Path, p.CommandLine))
        {
            case LlmKind.LlamaServer:
                string? model = LlmServer.Parse(p.Pid, cmd)?.ModelShort;
                return new("llm", model is null ? "Local LLM" : $"Local LLM · {model}", WorkloadKind.LocalLlm);
            case LlmKind.Ollama:
                return new("llm:ollama", "Local LLM · Ollama", WorkloadKind.LocalLlm);
            case LlmKind.LmStudio:
                return new("llm:lmstudio", "Local LLM · LM Studio", WorkloadKind.LocalLlm);
        }

        foreach (var o in _overrides)
            if (name.Contains(o.Match, StringComparison.OrdinalIgnoreCase) || path.Contains(o.Match, StringComparison.OrdinalIgnoreCase))
                return new("custom:" + o.Label.ToLowerInvariant(), o.Label, WorkloadKind.App);

        if (GameRules.TryGetGame(path, name, out string game))
            return new("game:" + game.ToLowerInvariant(), game, WorkloadKind.Game);

        if (_jobs.Find(cmd, name) is var (rule, label))
            return new("job:" + rule.Name.ToLowerInvariant(), label, rule.Notify ? WorkloadKind.Job : WorkloadKind.Service, Job: rule);

        if (lower == "claude.exe")
        {
            // The desktop app lives in its Store package; each Claude Code session is a claude-code\ binary.
            // Each session is named after the project it works in, so two projects are two rows.
            if (path.Contains(@"\claude-code\", StringComparison.OrdinalIgnoreCase))
                return ClaudeCodeProject.Name(p.Folder, Home) is { } project
                    ? new("claude-code:" + project.ToLowerInvariant(), "Claude Code · " + project, WorkloadKind.ClaudeCode)
                    : new("claude-code", "Claude Code", WorkloadKind.ClaudeCode);
            return new("claude-app", "Claude", WorkloadKind.ClaudeApp);
        }

        if (AppNames.TryGetValue(lower, out var app)) return new("app:" + app.ToLowerInvariant(), app, KindOf(app));
        if (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\codex-runtimes\", StringComparison.OrdinalIgnoreCase))
            return new("app:codex", "Codex", WorkloadKind.AiApp);
        if (path.Contains(@"\OpenAI.ChatGPT", StringComparison.OrdinalIgnoreCase))
            return new("app:chatgpt", "ChatGPT", WorkloadKind.AiApp);
        if (path.Contains(@"\Android\", StringComparison.OrdinalIgnoreCase) || lower.StartsWith("qemu-system", StringComparison.Ordinal))
            return new("app:android studio", "Android Studio", WorkloadKind.App);
        if (path.Contains(@"\PowerToys\", StringComparison.OrdinalIgnoreCase) || lower.StartsWith("microsoft.cmdpal", StringComparison.Ordinal))
            return new("app:powertoys", "PowerToys", WorkloadKind.App);
        if (path.Contains(@"\Corsair\", StringComparison.OrdinalIgnoreCase))
            return new("app:icue", "iCUE", WorkloadKind.App);
        if (path.Contains(@"\Lian-Li\", StringComparison.OrdinalIgnoreCase))
            return new("app:l-connect", "L-Connect", WorkloadKind.App);
        if (path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Epic Online Services\", StringComparison.OrdinalIgnoreCase))
            return new("app:epic games", "Epic Games", WorkloadKind.App);
        if (path.Contains(@"\Steam\", StringComparison.OrdinalIgnoreCase))
            return new("app:steam", "Steam", WorkloadKind.App);

        if (WindowsNames.TryGetValue(lower, out var windows)) return new("win:" + windows.ToLowerInvariant(), windows, WorkloadKind.Windows);

        string bare = System.IO.Path.GetFileNameWithoutExtension(name);
        if (IsHostProcess(lower)) return new("app:" + bare.ToLowerInvariant(), bare, WorkloadKind.App, Generic: true, Host: true);

        if (p.Pid <= 4 || p.ParentPid <= 4 || SystemNames.Contains(lower)
            || path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase))
            return new("windows", "Windows & drivers", WorkloadKind.Windows);

        // No rule: name it after its executable, but let it join an app it was started by.
        return new("app:" + bare.ToLowerInvariant(), bare, WorkloadKind.App, Generic: true);
    }

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>System executables that just host someone else's work (a console for a game, a WebView for Teams).</summary>
    private static bool IsHostProcess(string lower) => lower is "conhost.exe" or "msedgewebview2.exe" or "dllhost.exe";

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "smss.exe",
        "fontdrvhost.exe", "registry", "memory compression", "secure system", "wudfhost.exe", "audiodg.exe",
        "spoolsv.exe", "wmiprvse.exe", "sihost.exe", "ctfmon.exe", "taskhostw.exe", "runtimebroker.exe",
    };

    private static WorkloadKind KindOf(string app) => app switch
    {
        "Terminal" or "File Explorer" => WorkloadKind.Shell,
        "Copilot" => WorkloadKind.AiApp,
        _ => WorkloadKind.App,
    };

    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["code.exe"] = "VS Code",
        ["rstudio.exe"] = "R / RStudio",
        ["rsession.exe"] = "R / RStudio",
        ["rsession-utf8.exe"] = "R / RStudio",
        ["rterm.exe"] = "R / RStudio",
        ["rgui.exe"] = "R / RStudio",
        ["studio64.exe"] = "Android Studio",
        ["captureone.exe"] = "Capture One",
        ["spotify.exe"] = "Spotify",
        ["discord.exe"] = "Discord",
        ["ms-teams.exe"] = "Teams",
        ["zoom.exe"] = "Zoom",
        ["olk.exe"] = "Outlook",
        ["outlook.exe"] = "Outlook",
        ["excel.exe"] = "Office",
        ["winword.exe"] = "Office",
        ["powerpnt.exe"] = "Office",
        ["onedrive.exe"] = "OneDrive",
        ["onedrive.sync.service.exe"] = "OneDrive",
        ["explorer.exe"] = "File Explorer",
        ["windowsterminal.exe"] = "Terminal",
        ["openconsole.exe"] = "Terminal",
        ["powershell.exe"] = "Terminal",
        ["pwsh.exe"] = "Terminal",
        ["bash.exe"] = "Terminal",
        ["cmd.exe"] = "Terminal",
        ["mscopilot_proxy.exe"] = "Copilot",
        ["copilot.exe"] = "Copilot",
        ["amdinstallmanager.exe"] = "AMD Software",
        ["radeonsoftware.exe"] = "AMD Software",
        ["gamebar.exe"] = "Xbox Game Bar",
        ["gamebarftserver.exe"] = "Xbox Game Bar",
        ["jusched.exe"] = "Java updater",
        ["jucheck.exe"] = "Java updater",
    };

    private static readonly Dictionary<string, string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msmpeng.exe"] = "Microsoft Defender",
        ["mpdefendercoreservice.exe"] = "Microsoft Defender",
        ["nissrv.exe"] = "Microsoft Defender",
        ["mpcmdrun.exe"] = "Microsoft Defender",
        ["tiworker.exe"] = "Windows Update",
        ["trustedinstaller.exe"] = "Windows Update",
        ["mousocoreworker.exe"] = "Windows Update",
        ["usocoreworker.exe"] = "Windows Update",
        ["wuauclt.exe"] = "Windows Update",
        ["searchindexer.exe"] = "Search indexing",
        ["searchprotocolhost.exe"] = "Search indexing",
        ["searchfilterhost.exe"] = "Search indexing",
        ["dwm.exe"] = "Desktop (dwm)",
    };
}
