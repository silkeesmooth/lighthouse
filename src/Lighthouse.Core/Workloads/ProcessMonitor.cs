using System.Diagnostics;
using Lighthouse.Core.Model;
using Lighthouse.Core.Sensors;

namespace Lighthouse.Core.Workloads;

/// <summary>A watched job's run (its outermost process), for the job tracker. <see cref="Label"/> is the row label.</summary>
public sealed record JobRunProcess(int Pid, long CreateTime, JobRule Rule, string Label, DateTime Started);

/// <summary>
/// One sample of every process. <see cref="Io"/> says where disk and network figures came from, and the totals the
/// rows must add up to: the kernel trace's sum over every process (some no longer running), or the I/O counters'.
/// </summary>
public sealed record ProcessSample(
    IReadOnlyList<ProcessUsage> Usage,
    IReadOnlyList<JobRunProcess> JobRuns,
    LlmServer? Llm)
{
    public IoSource Io { get; init; } = new(false, 0);
}

/// <summary>Disk and network per program: from the kernel trace (with network), or the I/O counters (disk only).</summary>
public sealed record IoSource(bool Traced, double DiskTotalBps);

/// <summary>
/// Reads the process table, works out CPU per process since the last call, and names every process. Processes this
/// one can't open (another account's, a service) are named from <paramref name="helperDetails"/>, the sensor helper,
/// which answers null while there is none; those are asked again once it is there.
/// </summary>
public sealed class ProcessMonitor(Classifier classifier,
    Func<IReadOnlyList<ProcessKey>, IReadOnlyList<ProcessDetail>?>? helperDetails = null)
{
    private readonly Dictionary<(int Pid, long Created), (ProcessFacts Facts, Classification Own)> _known = [];
    private readonly HashSet<(int Pid, long Created)> _unresolved = [];
    private Classifier _classifier = classifier;

    /// <summary>New naming rules (settings.json changed): every process is named again on the next sample.</summary>
    public Classifier Classifier
    {
        set
        {
            _classifier = value;
            _known.Clear();
        }
    }
    private Dictionary<(int, long), long> _lastCpu = [];
    private Dictionary<(int, long), long> _lastIo = [];
    private long _lastStamp;
    private static readonly int LogicalProcessors = Environment.ProcessorCount;

    /// <param name="traced">Per-process disk and network rates from the sensor helper's kernel trace, or null without
    /// one; then disk comes from the processes' own I/O counters and network per program is unknown.</param>
    public ProcessSample Sample(IReadOnlyDictionary<int, GpuProcessUse> gpuByPid, IReadOnlyDictionary<int, ProcessIo>? traced = null)
    {
        var table = ProcessTable.Read();
        long stamp = Stopwatch.GetTimestamp();
        double elapsed100ns = _lastStamp == 0 ? 0 : (stamp - _lastStamp) * 1e7 / Stopwatch.Frequency;

        var facts = new Dictionary<int, ProcessFacts>(table.Count);
        var own = new Dictionary<int, Classification>(table.Count);
        var seen = new HashSet<(int, long)>();
        var cpuNow = new Dictionary<(int, long), long>(table.Count);
        var ioNow = new Dictionary<(int, long), long>(table.Count);

        var fresh = new List<ProcessFacts>();
        foreach (var proc in table)
        {
            if (proc.Pid == 0) continue; // Idle
            var key = (proc.Pid, proc.CreateTime);
            seen.Add(key);
            cpuNow[key] = proc.CpuTime;
            ioNow[key] = proc.IoBytes;
            if (_known.ContainsKey(key)) continue;
            var (path, cmd) = ProcessTable.Details(proc.Pid);
            // A Claude Code session is named after its working folder, so read that for those alone.
            string? folder = path?.Contains(@"\claude-code\", StringComparison.OrdinalIgnoreCase) == true
                ? ProcessTable.CurrentDirectory(proc.Pid) : null;
            var f = new ProcessFacts(proc.Pid, proc.ParentPid, proc.Name, path, cmd, proc.CreateTime, Folder: folder);
            fresh.Add(f);
            _known[key] = (f, _classifier.Classify(f));
            if (NeedsHelper(f)) _unresolved.Add(key);
        }
        foreach (var gone in _known.Keys.Where(k => !seen.Contains(k)).ToList()) _known.Remove(gone);
        _unresolved.IntersectWith(seen);
        AskHelper();

        foreach (var proc in table)
        {
            if (proc.Pid == 0) continue;
            var entry = _known[(proc.Pid, proc.CreateTime)];
            facts[proc.Pid] = entry.Facts;
            own[proc.Pid] = entry.Own;
        }

        var resolved = Resolver.Resolve(facts, own);
        var usage = new List<ProcessUsage>(table.Count);
        foreach (var proc in table)
        {
            if (proc.Pid == 0) continue;
            var key = (proc.Pid, proc.CreateTime);
            double cpu = 0;
            if (elapsed100ns > 0 && _lastCpu.TryGetValue(key, out long before))
                cpu = Math.Max(0, (proc.CpuTime - before) / (elapsed100ns * LogicalProcessors));
            var gpu = gpuByPid.GetValueOrDefault(proc.Pid);
            double disk = 0, down = 0, up = 0;
            if (traced is not null)
            {
                if (traced.TryGetValue(proc.Pid, out var t)) (disk, down, up) = (t.DiskBps, t.NetDownBps, t.NetUpBps);
            }
            else if (elapsed100ns > 0 && _lastIo.TryGetValue(key, out long ioBefore))
            {
                disk = Math.Max(0, (proc.IoBytes - ioBefore) / (elapsed100ns / 1e7));
            }
            usage.Add(new ProcessUsage(proc.Pid, proc.Name, resolved[proc.Pid], cpu, proc.PrivateWorkingSet,
                proc.WorkingSet - proc.PrivateWorkingSet, gpu?.VramBytes ?? 0, gpu?.ByEngine ?? EmptyEngines, DateTime.FromFileTime(proc.CreateTime),
                disk, down, up));
        }
        _lastCpu = cpuNow;
        _lastIo = ioNow;
        _lastStamp = stamp;

        var runs = resolved.Values
            .Where(r => r.Kind == WorkloadKind.Job && r.Job is not null)
            .DistinctBy(r => r.RootPid)
            .Where(r => facts.ContainsKey(r.RootPid))
            .Select(r => new JobRunProcess(r.RootPid, facts[r.RootPid].CreateTime, r.Job!, r.Label,
                DateTime.FromFileTime(facts[r.RootPid].CreateTime)))
            .ToList();

        var llm = LlmServer.Find(facts.Values, Environment.GetEnvironmentVariable("OLLAMA_HOST"));

        var io = traced is not null
            ? new IoSource(true, traced.Values.Sum(p => p.DiskBps))
            : new IoSource(false, usage.Sum(u => u.DiskBps));
        return new ProcessSample(usage, runs, llm) { Io = io };
    }

    /// <summary>No path (not ours to open), or the LLM server without its command line.</summary>
    private static bool NeedsHelper(ProcessFacts f) =>
        f.Path is null || (f.CommandLine is null && f.Name.Equals(SensorHandler.LlmServerExe, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names the processes this one couldn't open, once the helper answers; renames them with what it says.</summary>
    private void AskHelper()
    {
        if (helperDetails is null || _unresolved.Count == 0) return;
        var keys = _unresolved.Select(k => new ProcessKey(k.Pid, k.Created)).ToList();
        if (helperDetails(keys) is not { } answers) return; // no helper yet: ask again next time
        foreach (var d in answers)
        {
            var key = (d.Pid, d.CreateTime);
            if (!_known.TryGetValue(key, out var entry)) continue;
            var f = entry.Facts with { Path = entry.Facts.Path ?? d.Path, CommandLine = entry.Facts.CommandLine ?? d.CommandLine, Owner = d.Owner };
            _known[key] = (f, _classifier.Classify(f));
        }
        _unresolved.Clear(); // answered, or the helper doesn't know either
    }

    private static readonly IReadOnlyDictionary<int, double> EmptyEngines = new Dictionary<int, double>();
}
