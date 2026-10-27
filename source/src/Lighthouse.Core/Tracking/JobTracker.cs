using Lighthouse.Core.Native;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Core.Tracking;

public static class Durations
{
    /// <summary>"2h 14m", "3m 05s", "42s".</summary>
    public static string Format(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s" : $"{d.TotalSeconds:0}s";

    /// <summary>A typical time, without false precision: "2h 10m", "44 min", "under a minute".</summary>
    public static string Rough(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m"
        : d.TotalMinutes >= 1 ? $"{Math.Round(d.TotalMinutes):0} min" : "under a minute";
}

public sealed record JobFinished(JobRule Rule, string Label, TimeSpan Duration, int? ExitCode)
{
    /// <summary>The label without the job's name: "load --region west" from "Import: load --region west".</summary>
    public string Detail => Label.StartsWith(Rule.Name + ": ", StringComparison.Ordinal) ? Label[(Rule.Name.Length + 2)..] : "";

    public bool Succeeded => ExitCode == 0;

    public string Message
    {
        get
        {
            string what = Detail.Length > 0 ? $"{Rule.Name} `{Detail}`" : Rule.Name;
            return ExitCode is { } code
                ? $"{what} {JobText.Describe(code, Rule.ExitCodes)} after {Durations.Format(Duration)}."
                : $"{what} ended after {Durations.Format(Duration)}.";
        }
    }
}

/// <summary>
/// Holds a handle on each job run's outermost process so its exit code is still readable after it exits.
/// A launcher that waits for a child and exits with the child's code (a virtualenv re-launch, say) reports the
/// run's real code this way.
/// </summary>
public sealed class JobTracker : IDisposable
{
    private sealed record Run(JobRunProcess Process, IntPtr Handle);

    private readonly Dictionary<(int, long), Run> _runs = [];

    public IReadOnlyList<JobRunProcess> Active => _runs.Values.Select(r => r.Process).ToList();

    public List<JobFinished> Update(IReadOnlyList<JobRunProcess> roots)
    {
        foreach (var root in roots)
        {
            var key = (root.Pid, root.CreateTime);
            if (_runs.ContainsKey(key)) continue;
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.SYNCHRONIZE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, root.Pid);
            _runs[key] = new Run(root, handle);
        }

        var finished = new List<JobFinished>();
        var live = roots.Select(r => (r.Pid, r.CreateTime)).ToHashSet();
        foreach (var (key, run) in _runs.ToList())
        {
            bool exited = run.Handle != IntPtr.Zero
                ? NativeMethods.WaitForSingleObject(run.Handle, 0) == NativeMethods.WAIT_OBJECT_0
                : !live.Contains(key);
            if (!exited) continue;
            int? code = run.Handle != IntPtr.Zero && NativeMethods.GetExitCodeProcess(run.Handle, out int c) ? c : null;
            var p = run.Process;
            finished.Add(new JobFinished(p.Rule, p.Label, DateTime.Now - p.Started, code));
            if (run.Handle != IntPtr.Zero) NativeMethods.CloseHandle(run.Handle);
            _runs.Remove(key);
        }
        return finished;
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values)
            if (run.Handle != IntPtr.Zero) NativeMethods.CloseHandle(run.Handle);
        _runs.Clear();
    }
}
