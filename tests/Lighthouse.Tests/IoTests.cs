using Lighthouse.Core;
using Lighthouse.Core.Model;
using Lighthouse.Core.Network;
using Lighthouse.Core.Sensors;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

public class IoCounterTests
{
    private static byte[] Payload(int length, params (int Offset, int Value)[] fields)
    {
        var bytes = new byte[length];
        foreach (var (offset, value) in fields) BitConverter.GetBytes(value).CopyTo(bytes, offset);
        return bytes;
    }

    [Fact]
    public void Network_bytes_go_to_the_process_named_in_the_event()
    {
        var counter = new IoCounter();
        counter.Add(IoCounter.TcpIp, 11, -1, Payload(44, (0, 4200), (4, 1500)), 44); // receive, IPv4
        counter.Add(IoCounter.TcpIp, 27, -1, Payload(68, (0, 4200), (4, 500)), 68);  // receive, IPv6
        counter.Add(IoCounter.UdpIp, 10, -1, Payload(28, (0, 4200), (4, 300)), 28);  // send, UDP
        counter.Add(IoCounter.TcpIp, 12, -1, Payload(44, (0, 4200), (4, 999)), 44);  // another TCP event: not a byte count
        var rate = counter.Rates(DateTime.UtcNow.AddSeconds(1)).Single();
        Assert.Equal(4200, rate.Pid);
        Assert.InRange(rate.NetDownBps, 1500, 2000); // 2000 bytes over about a second
        Assert.InRange(rate.NetUpBps, 250, 300);
        Assert.Equal(0, rate.DiskBps);
    }

    [Fact]
    public void Disk_bytes_go_to_the_process_that_issued_them()
    {
        var counter = new IoCounter(lookupThread: tid => tid == 77 ? 9100 : 0);
        counter.Add(IoCounter.Thread, 1, 0, Payload(8, (0, 5300), (4, 66)), 8);       // thread 66 starts in 5300
        counter.Add(IoCounter.DiskIo, 10, -1, Payload(52, (8, 4096), (48, 66)), 52);  // a read issued by thread 66
        counter.Add(IoCounter.DiskIo, 11, -1, Payload(52, (8, 8192), (48, 77)), 52);  // a write by a thread found by lookup
        counter.Add(IoCounter.DiskIo, 11, -1, Payload(52, (8, 1000), (48, 99)), 52);  // nobody to name: pid 0
        var rates = counter.Rates(DateTime.UtcNow.AddSeconds(1)).ToDictionary(r => r.Pid);
        Assert.True(rates[5300].DiskBps > 0 && rates[9100].DiskBps > rates[5300].DiskBps);
        Assert.True(rates.ContainsKey(0));
        Assert.Empty(counter.Rates(DateTime.UtcNow.AddSeconds(2))); // counting starts again after each read
    }

    [Fact]
    public void Short_or_unknown_events_are_ignored()
    {
        var counter = new IoCounter();
        counter.Add(IoCounter.TcpIp, 11, -1, Payload(4), 4);
        counter.Add(IoCounter.DiskIo, 10, -1, Payload(40, (8, 4096)), 40); // too short to carry the issuing thread
        counter.Add(0x12345678, 10, 4, Payload(64), 64);
        Assert.Empty(counter.Rates(DateTime.UtcNow.AddSeconds(1)));
    }
}

public class IoRowsTests
{
    private static ProcessUsage P(int pid, string key, double disk, double down, double up, WorkloadKind kind = WorkloadKind.App) =>
        new(pid, $"p{pid}.exe", new Resolved(key, key, kind, pid), 0.01, 1 << 20, 0, 0, new Dictionary<int, double>(), DateTime.Now,
            disk, down, up);

    [Fact]
    public void Disk_and_network_rows_plus_everything_else_equal_the_totals()
    {
        var usage = new[]
        {
            P(1, "app:steam", 40e6, 5e6, 0.1e6),
            P(2, "app:chrome", 2e6, 1e6, 0.2e6),
            P(3, "app:quiet", 0, 0, 0),
        };
        // Totals: disk by the same measure plus a process that has exited; network as the adapters saw it.
        var io = new IoTotals(true, 45e6, 6.5e6, 0.5e6);
        var table = Aggregator.Aggregate(usage, 10, 8, 1, io: io);
        double Sum(Func<WorkloadUsage, double> get) => table.TopIo.Sum(get) + get(table.RestIo!);
        Assert.Equal(45, Sum(w => w.DiskMBps), 6);
        Assert.Equal(6.5e6 * 8 / 1e6, Sum(w => w.NetDownMbps), 6);
        Assert.Equal(0.5e6 * 8 / 1e6, Sum(w => w.NetUpMbps), 6);
        Assert.Equal(["app:steam", "app:chrome"], table.TopIo.Select(w => w.Key)); // busiest first, idle left out
    }

    [Fact]
    public void Rows_never_claim_more_than_the_total()
    {
        var usage = new[] { P(1, "app:a", 10e6, 0, 0), P(2, "app:b", 10e6, 0, 0) };
        var table = Aggregator.Aggregate(usage, 10, 8, 1, io: new IoTotals(true, 16e6, 0, 0)); // read a moment apart
        Assert.Equal(16, table.TopIo.Sum(w => w.DiskMBps), 6);
        Assert.Equal(0, table.RestIo!.DiskMBps, 6);
    }
}

public class BusyLineTests
{
    private static Snapshot With(params WorkloadUsage[] rows) => new() { IoTraced = true, AllWorkloads = rows };

    private static WorkloadUsage W(string label, double down) =>
        new(label, label, WorkloadKind.App, 1, 0, 1, 0, 1, NetDownMbps: down);

    [Fact]
    public void The_program_behind_most_of_the_traffic_is_named()
    {
        Assert.Equal("Local LLM tools", MonitorEngine.BusiestOnTheLine(With(W("Local LLM tools", 30), W("Chrome", 2)), 34));
        Assert.Null(MonitorEngine.BusiestOnTheLine(With(W("A", 10), W("B", 10), W("C", 10)), 34)); // no one stands out
        Assert.Null(MonitorEngine.BusiestOnTheLine(With(W("A", 30)) with { IoTraced = false }, 34)); // not known
    }

    [Fact]
    public void The_note_says_who_when_it_knows()
    {
        var busy = new SpeedResult(110, 9.1, 18, 21_000_000, null) { BackgroundDownMbps = 33.6, BackgroundBy = "Local LLM tools" };
        Assert.Contains("already downloading 34 Mbps when the test started (mostly Local LLM tools)", busy.BusyNote);
    }
}
