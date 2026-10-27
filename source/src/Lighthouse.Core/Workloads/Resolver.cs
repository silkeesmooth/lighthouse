using Lighthouse.Core.Model;

namespace Lighthouse.Core.Workloads;

/// <summary>A process's final workload, after the process tree has been taken into account.</summary>
public sealed record Resolved(string Key, string Label, WorkloadKind Kind, int RootPid, JobRule? Job = null);

/// <summary>
/// Groups processes by who started them. Owners (a game, a job run, the local LLM, a Claude Code session)
/// claim everything below them: the Chrome a job drives counts as that job, not as the person's own Chrome.
/// </summary>
public static class Resolver
{
    private const int MaxDepth = 10;

    public static Dictionary<int, Resolved> Resolve(IReadOnlyDictionary<int, ProcessFacts> facts,
        IReadOnlyDictionary<int, Classification> own)
    {
        var memo = new Dictionary<int, Resolved>();
        foreach (int pid in facts.Keys) Get(pid, facts, own, memo, 0);
        return memo;
    }

    private static ProcessFacts? Parent(ProcessFacts p, IReadOnlyDictionary<int, ProcessFacts> facts)
    {
        // A parent that started after its "child" is a reused pid, not the real parent.
        if (p.ParentPid == p.Pid || !facts.TryGetValue(p.ParentPid, out var parent)) return null;
        return parent.CreateTime <= p.CreateTime ? parent : null;
    }

    private static Resolved Get(int pid, IReadOnlyDictionary<int, ProcessFacts> facts,
        IReadOnlyDictionary<int, Classification> own, Dictionary<int, Resolved> memo, int depth)
    {
        if (memo.TryGetValue(pid, out var done)) return done;
        var p = facts[pid];
        var c = own[pid];
        Resolved result;

        if (WorkloadKinds.IsJob(c.Kind))
        {
            // One row per run: the root is the outermost process of the same job (a launcher that re-runs itself
            // in a virtualenv, say, is one run, not two).
            var root = p;
            for (var up = Parent(root, facts); up is not null && own[up.Pid].Key == c.Key; up = Parent(up, facts))
                root = up;
            var rootClass = own[root.Pid];
            result = new Resolved($"{c.Key}:{root.Pid}", rootClass.Label, c.Kind, root.Pid, c.Job);
        }
        else if (WorkloadKinds.IsOwner(c.Kind) || c.Kind == WorkloadKind.Lighthouse || depth >= MaxDepth)
        {
            result = new Resolved(c.Key, c.Label, c.Kind, pid);
        }
        else if (OwnerAbove(p, facts, own) is { } ownerPid)
        {
            result = Get(ownerPid, facts, own, memo, depth + 1);
        }
        else if (c.Host && Parent(p, facts) is { } hostParent)
        {
            result = Get(hostParent.Pid, facts, own, memo, depth + 1);
        }
        else if (c.Generic && AppAbove(p, facts, own, memo, depth) is { } app)
        {
            result = app;
        }
        else
        {
            result = new Resolved(c.Key, c.Label, c.Kind, pid);
        }

        memo[pid] = result;
        return result;
    }

    private static int? OwnerAbove(ProcessFacts p, IReadOnlyDictionary<int, ProcessFacts> facts,
        IReadOnlyDictionary<int, Classification> own)
    {
        int steps = 0;
        for (var up = Parent(p, facts); up is not null && steps < MaxDepth; up = Parent(up, facts), steps++)
            if (WorkloadKinds.IsOwner(own[up.Pid].Kind)) return up.Pid;
        return null;
    }

    /// <summary>The parent's workload, unless the parent is a shell or Windows (explorer starts everything).</summary>
    private static Resolved? AppAbove(ProcessFacts p, IReadOnlyDictionary<int, ProcessFacts> facts,
        IReadOnlyDictionary<int, Classification> own, Dictionary<int, Resolved> memo, int depth)
    {
        if (Parent(p, facts) is not { } up) return null;
        if (own[up.Pid].Kind is WorkloadKind.Shell or WorkloadKind.Windows or WorkloadKind.Lighthouse) return null;
        return Get(up.Pid, facts, own, memo, depth + 1);
    }
}
