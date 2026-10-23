namespace Lighthouse.Core.Model;

public enum WorkloadKind
{
    Game,
    /// <summary>A run of a watched job (settings.json "jobs"): its own row, a boat, a toast when it ends.</summary>
    Job,
    /// <summary>A watched job set to notify: false, such as a dev server: its own row, nothing else.</summary>
    Service,
    LocalLlm,
    ClaudeCode,
    ClaudeApp,
    /// <summary>Other AI apps: Codex, ChatGPT, Copilot.</summary>
    AiApp,
    App,
    Shell,
    Windows,
    Lighthouse,
}

public static class WorkloadKinds
{
    /// <summary>Kinds that own whatever they launch: the Chrome a job drives counts as that job.</summary>
    public static bool IsOwner(WorkloadKind kind) => kind is WorkloadKind.Game or WorkloadKind.Job
        or WorkloadKind.Service or WorkloadKind.LocalLlm or WorkloadKind.ClaudeCode;

    /// <summary>The legend's groups, one dot colour each.</summary>
    public static bool IsAi(WorkloadKind kind) => kind is WorkloadKind.LocalLlm or WorkloadKind.ClaudeCode
        or WorkloadKind.ClaudeApp or WorkloadKind.AiApp;

    public static bool IsJob(WorkloadKind kind) => kind is WorkloadKind.Job or WorkloadKind.Service;
}

/// <summary>One row of "Using it now". CPU is % of the whole machine, scaled to Task Manager's total.</summary>
public sealed record WorkloadUsage(
    string Key,
    string Label,
    WorkloadKind Kind,
    double CpuPct,
    double GpuPct,
    double RamGb,
    double VramGb,
    int Processes,
    DateTime? Started = null,
    string? Note = null,
    double DiskMBps = 0,
    double NetDownMbps = 0,
    double NetUpMbps = 0);

public enum CcdState
{
    Unknown,
    /// <summary>Nothing parked: both CCDs are taking work.</summary>
    BothActive,
    /// <summary>The non-cache CCD is parked; games run on the V-Cache CCD.</summary>
    OtherParked,
    /// <summary>The V-Cache CCD is parked (the driver prefers frequency).</summary>
    CacheParked,
    Partial,
}

public sealed record Snapshot
{
    public DateTime Time { get; init; } = DateTime.Now;

    public double CpuLoad { get; init; }
    public double? CpuTemp { get; init; }
    public double? CacheCcdTemp { get; init; }
    public double? OtherCcdTemp { get; init; }
    public double? CpuPower { get; init; }
    public double? CpuClockMhz { get; init; }

    public double GpuLoad { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuMemTemp { get; init; }
    public double? GpuPower { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? GpuMemClockMhz { get; init; }
    public double VramUsedGb { get; init; }
    public double VramTotalGb { get; init; }

    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double CommitPct { get; init; }
    public double? DimmTemp { get; init; }

    public double? NvmeTemp { get; init; }
    public double? NvmeWearPct { get; init; }
    public double DiskActivePct { get; init; }
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }

    public CcdState Ccd { get; init; }
    /// <summary>CPU, RAM and SSD sensors are readable: the sensor helper answers (or the app is admin itself).</summary>
    public bool FullSensors { get; init; }

    /// <summary>The top rows shown in the flyout.</summary>
    public IReadOnlyList<WorkloadUsage> Workloads { get; init; } = [];
    /// <summary>Everything not in <see cref="Workloads"/>, so rows + Rest = the totals.</summary>
    public WorkloadUsage? Rest { get; init; }
    /// <summary>The top rows by disk and network, for the flyout's "Disk & network" view, and what is left of those totals.</summary>
    public IReadOnlyList<WorkloadUsage> IoWorkloads { get; init; } = [];
    public WorkloadUsage? IoRest { get; init; }
    /// <summary>All disk reads and writes, MB/s, by the same measure as the rows.</summary>
    public double DiskMBps { get; init; }
    /// <summary>Network per program comes from the sensor helper's kernel trace; without it only totals are known.</summary>
    public bool IoTraced { get; init; }
    /// <summary>Every workload, small ones included (alerts look at all of them).</summary>
    public IReadOnlyList<WorkloadUsage> AllWorkloads { get; init; } = [];
    public WorkloadUsage? Self { get; init; }

    public WorkloadUsage? Game => AllWorkloads.FirstOrDefault(w => w.Kind == WorkloadKind.Game);
    public WorkloadUsage? LocalLlm => AllWorkloads.FirstOrDefault(w => w.Kind == WorkloadKind.LocalLlm);
}
