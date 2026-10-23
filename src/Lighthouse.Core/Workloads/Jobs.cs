using System.Text.RegularExpressions;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Workloads;

/// <summary>
/// A script or pipeline to watch, from settings.json "jobs". Every process whose command line matches
/// <see cref="Match"/> belongs to the job: each run gets its own row (with everything it starts) and a boat in
/// the scene, and a toast when it ends that says what its exit code means.
/// </summary>
public sealed record JobRule
{
    public string Name { get; init; } = "Job";

    /// <summary>Regular expression on the command line, case-insensitive.</summary>
    public string Match { get; init; } = "";

    /// <summary>
    /// Optional regular expression on the program's file name ("python"). Without it, a shell that merely
    /// launches the job (bash -c "python -m pkg ...") would match too and take the run's row.
    /// </summary>
    public string? Process { get; init; }

    /// <summary>Optional regular expression with a (?&lt;label&gt;...) group for the row label. Default: the command's arguments.</summary>
    public string? Label { get; init; }

    /// <summary>What exit codes mean for this job, e.g. "4": "stopped: spend cap or outage". 0 is "finished".</summary>
    public Dictionary<string, string> ExitCodes { get; init; } = [];

    /// <summary>False for servers and other long-lived helpers: a row of their own, but no boat and no toast.</summary>
    public bool Notify { get; init; } = true;
}

/// <summary>The settings' job rules, compiled. A rule whose pattern doesn't compile is skipped and logged.</summary>
public sealed class JobMatcher
{
    private readonly List<(JobRule Rule, Regex Match, Regex? Process, Regex? Label)> _rules = [];

    public JobMatcher(IEnumerable<JobRule>? rules)
    {
        foreach (var rule in rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Match)) continue;
            try
            {
                const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                var timeout = TimeSpan.FromMilliseconds(50);
                Regex? Optional(string? pattern) => pattern is { Length: > 0 } ? new Regex(pattern, options, timeout) : null;
                _rules.Add((rule, new Regex(rule.Match, options, timeout), Optional(rule.Process), Optional(rule.Label)));
            }
            catch (ArgumentException e)
            {
                Log.Write($"job '{rule.Name}' skipped, its pattern doesn't compile: {e.Message}");
            }
        }
    }

    /// <summary>The first rule the process matches, with the row label ("Name: label", or just the name).</summary>
    public (JobRule Rule, string Label)? Find(string? commandLine, string processName)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        foreach (var (rule, match, process, label) in _rules)
        {
            try
            {
                if (process is not null && !process.IsMatch(processName)) continue;
                if (!match.IsMatch(commandLine)) continue;
                string text = label?.Match(commandLine) is { Success: true } m && m.Groups["label"].Success
                    ? m.Groups["label"].Value
                    : JobText.DefaultLabel(commandLine);
                return (rule, text.Length > 0 ? $"{rule.Name}: {text}" : rule.Name);
            }
            catch (RegexMatchTimeoutException)
            {
                // A runaway pattern on one odd command line: treat as no match.
            }
        }
        return null;
    }
}

public static class JobText
{
    private static readonly string[] ScriptExtensions =
        [".py", ".js", ".mjs", ".cjs", ".ts", ".r", ".ps1", ".sh", ".rb", ".pl", ".jl", ".bat", ".cmd"];

    private static readonly HashSet<string> EntryModules = new(StringComparer.OrdinalIgnoreCase) { "cli", "main", "__main__" };

    public const int MaxLabel = 100;

    /// <summary>
    /// The interesting part of a command line:
    /// "python -m pkg load --region west" -> "load --region west"; "python -m pkg.backfill run x" -> "backfill run x";
    /// "python train.py --epochs 3" -> "train.py --epochs 3"; anything else -> its arguments.
    /// </summary>
    public static string DefaultLabel(string commandLine)
    {
        string[] args = Redact(NativeMethods.SplitArgs(commandLine));
        if (args.Length <= 1) return "";
        string text;
        int module = Array.IndexOf(args, "-m");
        int script = Array.FindIndex(args, 1, a => ScriptExtensions.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        if (module >= 1 && module + 1 < args.Length)
        {
            string name = args[module + 1];
            int dot = name.LastIndexOf('.');
            string sub = dot > 0 && !EntryModules.Contains(name[(dot + 1)..]) ? name[(dot + 1)..] + " " : "";
            text = sub + string.Join(" ", args[(module + 2)..]);
        }
        else if (script >= 1)
        {
            text = Path.GetFileName(args[script]) + " " + string.Join(" ", args[(script + 1)..]);
        }
        else
        {
            text = string.Join(" ", args[1..]);
        }
        // Rows wrap, so real commands show whole; the cap only stops an inline script filling the flyout.
        text = text.Trim();
        return text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)] + "…";
    }

    /// <summary>The words that make an argument a secret: --token abc, --password=abc, API_KEY=abc.</summary>
    private static readonly Regex SecretName = new(@"(pass(word|wd|phrase)?|secret|token|api[-_]?key|apikey|auth|credential|private[-_]?key)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const string Hidden = "•••";

    /// <summary>
    /// The arguments with secrets hidden: the value after a flag named like one (--token abc), after "=" in one
    /// (--password=abc, API_KEY=abc). Labels show in the flyout, toasts and History, so a secret never gets there.
    /// </summary>
    public static string[] Redact(string[] args)
    {
        var shown = (string[])args.Clone();
        for (int i = 1; i < shown.Length; i++)
        {
            string a = shown[i];
            int eq = a.IndexOf('=');
            if (eq > 0 && SecretName.IsMatch(a[..eq]))
                shown[i] = a[..(eq + 1)] + Hidden;
            else if (a.StartsWith('-') && SecretName.IsMatch(a) && i + 1 < shown.Length && !shown[i + 1].StartsWith('-'))
                shown[++i] = Hidden;
        }
        return shown;
    }

    /// <summary>
    /// The command a run is ("launch" from "ETL: load --region west"): the first word of the run that isn't a flag,
    /// so one job's different commands are timed apart. "" for a run with no arguments.
    /// </summary>
    public static string RunKind(string label)
    {
        int colon = label.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return "";
        return label[(colon + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => !w.StartsWith('-')) ?? "";
    }

    /// <summary>How long runs like this usually take: the median of the last 5 that finished, null with fewer than 2.</summary>
    public static TimeSpan? Usual(IEnumerable<TimeSpan> recentFinished)
    {
        var last = recentFinished.Take(5).OrderBy(d => d).ToList();
        if (last.Count < 2) return null;
        return last.Count % 2 == 1 ? last[last.Count / 2] : (last[last.Count / 2 - 1] + last[last.Count / 2]) / 2;
    }

    /// <summary>
    /// "usually 2h 10m · about 40 min left", "usually 2h 10m · should finish soon", or past half again the usual time
    /// "running longer than usual (usually 2h 10m)". Null when there is no usual yet.
    /// </summary>
    public static string? Progress(TimeSpan elapsed, TimeSpan? usual)
    {
        if (usual is not { } u) return null;
        string usually = $"usually {Tracking.Durations.Rough(u)}";
        if (elapsed > u * 1.5) return $"running longer than usual ({usually})";
        var left = u - elapsed;
        if (left <= TimeSpan.FromMinutes(1)) return $"{usually} · should finish soon";
        string about = left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:00}m" : $"{Math.Ceiling(left.TotalMinutes):0} min";
        return $"{usually} · about {about} left";
    }

    /// <summary>What an exit code means: the job's own meanings first, then the few every program shares.</summary>
    public static string Describe(int code, IReadOnlyDictionary<string, string>? meanings = null)
    {
        if (meanings is not null && meanings.TryGetValue(code.ToString(System.Globalization.CultureInfo.InvariantCulture), out var meaning))
            return meaning;
        return code switch
        {
            0 => "finished",
            -1073741510 => "was interrupted (Ctrl+C)",
            _ => $"ended with exit {code}",
        };
    }
}
