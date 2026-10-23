using System.Globalization;
using System.Text.RegularExpressions;

namespace Lighthouse.Core.Sensors;

/// <summary>GPU use of one process on the main GPU: utilization per engine index (0-100 each).</summary>
public sealed record GpuProcessUse(IReadOnlyDictionary<int, double> ByEngine, double VramBytes);

public sealed record CounterReading(
    double CpuUtility,
    IReadOnlyDictionary<int, bool> Parked,
    double GpuLoad,
    IReadOnlyDictionary<int, GpuProcessUse> GpuByPid,
    double VramUsedBytes,
    double DiskActivePct,
    double NetDownBytesPerSec,
    double NetUpBytesPerSec,
    double CommitPct);

/// <summary>Everything Lighthouse reads from PDH, in one query collected once per tick.</summary>
public sealed partial class CounterSampler : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _cpu, _parking, _gpuEngine, _gpuAdapterMem, _gpuProcMem, _diskIdle, _netDown, _netUp, _commit;
    private readonly string? _luid;

    public CounterSampler(GpuAdapter? gpu)
    {
        _luid = gpu?.PdhLuid;
        _cpu = _query.Add(@"\Processor Information(_Total)\% Processor Utility");
        _parking = _query.Add(@"\Processor Information(*)\Parking Status");
        _gpuEngine = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuAdapterMem = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuProcMem = _query.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _diskIdle = _query.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _netDown = _query.Add(@"\Network Interface(*)\Bytes Received/sec");
        _netUp = _query.Add(@"\Network Interface(*)\Bytes Sent/sec");
        _commit = _query.Add(@"\Memory\% Committed Bytes In Use");
        _query.Collect(); // rate counters need a first sample to diff against
    }

    public CounterReading Sample()
    {
        _query.Collect();

        var parked = new Dictionary<int, bool>();
        foreach (var (instance, value) in _parking?.Array() ?? [])
        {
            // "0,12" = group 0, processor 12. Skip "_Total" and "0,_Total".
            int comma = instance.IndexOf(',');
            if (comma < 0 || !int.TryParse(instance.AsSpan(comma + 1), out int cpu)) continue;
            int group = int.TryParse(instance.AsSpan(0, comma), out int g) ? g : 0;
            parked[group * 64 + cpu] = value >= 0.5;
        }

        var engineByPid = new Dictionary<int, Dictionary<int, double>>();
        var engineTotals = new Dictionary<int, double>();
        foreach (var (instance, value) in _gpuEngine?.Array() ?? [])
        {
            var m = EngineInstance().Match(instance);
            if (!m.Success || !IsMainGpu(m.Groups["luid"].Value)) continue;
            int pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
            int engine = int.Parse(m.Groups["eng"].Value, CultureInfo.InvariantCulture);
            if (!engineByPid.TryGetValue(pid, out var engines)) engineByPid[pid] = engines = [];
            engines[engine] = engines.GetValueOrDefault(engine) + value;
            engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + value;
        }

        var vramByPid = new Dictionary<int, double>();
        foreach (var (instance, value) in _gpuProcMem?.Array() ?? [])
        {
            var m = ProcessMemInstance().Match(instance);
            if (!m.Success || !IsMainGpu(m.Groups["luid"].Value)) continue;
            int pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
            vramByPid[pid] = vramByPid.GetValueOrDefault(pid) + value;
        }

        var gpuByPid = new Dictionary<int, GpuProcessUse>();
        foreach (int pid in engineByPid.Keys.Union(vramByPid.Keys))
            gpuByPid[pid] = new GpuProcessUse(engineByPid.GetValueOrDefault(pid) ?? [], vramByPid.GetValueOrDefault(pid));

        double vramUsed = 0;
        foreach (var (instance, value) in _gpuAdapterMem?.Array() ?? [])
            if (IsMainGpu(instance)) vramUsed += value;

        return new CounterReading(
            CpuUtility: Math.Clamp(_cpu?.Value() ?? 0, 0, 100),
            Parked: parked,
            // Task Manager's GPU figure: the busiest engine.
            GpuLoad: Math.Clamp(engineTotals.Count == 0 ? 0 : engineTotals.Values.Max(), 0, 100),
            GpuByPid: gpuByPid,
            VramUsedBytes: vramUsed,
            DiskActivePct: Math.Clamp(100 - (_diskIdle?.Value() ?? 100), 0, 100),
            NetDownBytesPerSec: (_netDown?.Array() ?? []).Sum(x => x.Value),
            NetUpBytesPerSec: (_netUp?.Array() ?? []).Sum(x => x.Value),
            CommitPct: _commit?.Value() ?? 0);
    }

    private bool IsMainGpu(string text) => _luid is null || text.Contains(_luid, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^pid_(?<pid>\d+)_(?<luid>luid_0x[0-9a-f]+_0x[0-9a-f]+)_phys_\d+_eng_(?<eng>\d+)_engtype_", RegexOptions.IgnoreCase)]
    private static partial Regex EngineInstance();

    [GeneratedRegex(@"^pid_(?<pid>\d+)_(?<luid>luid_0x[0-9a-f]+_0x[0-9a-f]+)_phys_\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessMemInstance();

    public void Dispose() => _query.Dispose();
}
