using System.IO.Pipes;
using System.Text;
using Lighthouse.Core.Sensors;

namespace Lighthouse.Tests;

public class SensorHandlerTests
{
    private readonly List<int> _killed = [];

    private static readonly List<ProcInfo> Table =
    [
        new(100, 4, "llama-server.exe", 1000, 0, 0, 0),
        new(200, 4, "svchost.exe", 2000, 0, 0, 0),
    ];

    private SensorHandler Handler() => new(
        () => new HardwareReading { CpuTemp = 47 },
        () => null,
        () => (null, "no trace in tests"),
        () => Table,
        pid => pid == 100
            ? (@"C:\llm\bin\llama-server.exe", @"C:\llm\bin\llama-server.exe --port 8081 --api-key-file C:\llm\key.txt")
            : (@"C:\Windows\System32\svchost.exe", @"C:\Windows\System32\svchost.exe -k netsvcs -p -s Schedule"),
        pid => pid == 100 ? @"PC\llm-runner" : "NT AUTHORITY\\SYSTEM",
        pid => _killed.Add(pid),
        () => ["a notice"],
        () => ["a log line"]);

    [Fact]
    public void Paths_for_any_process_but_command_lines_only_for_the_llm_server()
    {
        var r = Handler().Handle(new SensorRequest { Op = "details", Processes = [new(100, 1000), new(200, 2000)] });
        var llm = r.Details!.Single(d => d.Pid == 100);
        var service = r.Details!.Single(d => d.Pid == 200);
        Assert.Equal(("llm-runner", true), (llm.Owner?.Split('\\')[1], llm.CommandLine!.Contains("--port 8081")));
        Assert.Equal(@"C:\Windows\System32\svchost.exe", service.Path);
        Assert.Null(service.CommandLine); // another account's command line is never handed out
        Assert.Null(service.Owner);
    }

    [Fact]
    public void A_reused_pid_gets_no_answer()
    {
        var r = Handler().Handle(new SensorRequest { Op = "details", Processes = [new(100, 999)] });
        Assert.Empty(r.Details!);
    }

    [Fact]
    public void Only_a_running_llama_server_can_be_stopped()
    {
        var handler = Handler();
        Assert.False(handler.Handle(new SensorRequest { Op = "stop-llm", Processes = [new(200, 2000)] }).Ok); // svchost
        Assert.False(handler.Handle(new SensorRequest { Op = "stop-llm", Processes = [new(100, 999)] }).Ok); // stale
        Assert.False(handler.Handle(new SensorRequest { Op = "stop-llm" }).Ok);
        Assert.Empty(_killed);
        Assert.True(handler.Handle(new SensorRequest { Op = "stop-llm", Processes = [new(100, 1000)] }).Ok);
        Assert.Equal([100], _killed);
    }

    [Fact]
    public void Readings_carry_the_helpers_notices_and_log_and_unknown_requests_are_refused()
    {
        var r = Handler().Handle(new SensorRequest { Op = "reading" });
        Assert.Equal((47.0, "a notice", "a log line"), (r.Reading!.CpuTemp, r.Notices!.Single(), r.Log!.Single()));
        Assert.False(Handler().Handle(new SensorRequest { Op = "run", Processes = [new(200, 2000)] }).Ok);
    }
}

public class SensorPipeTests
{
    private static string NewPipe() => "Lighthouse.Tests." + Guid.NewGuid().ToString("N");

    private static SensorHandler Fake(double cpuTemp) => new(
        () => new HardwareReading { CpuTemp = cpuTemp, CcdTemps = [61, 48] },
        () => null,
        () => ([new ProcessIo(7, 1e6, 0, 0)], null),
        () => [new ProcInfo(Environment.ProcessId, 4, "testhost.exe", 1, 0, 0, 0)],
        _ => (Environment.ProcessPath, null),
        _ => null,
        _ => { },
        () => [],
        () => []);

    [Fact]
    public async Task The_app_reads_through_the_pipe_from_a_helper_running_the_same_exe()
    {
        string name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new SensorServer(Fake(52), name).RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        using var client = new SensorClient(null, name, helperTask: null, readLocally: false);
        await WaitFor(() => client.Sample().CpuTemp == 52);
        Assert.True(client.Connected);
        Assert.False(client.Full); // the test helper isn't admin, and says so
        Assert.Equal("the sensor helper isn't running as admin", client.Error);
        Assert.Equal([61.0, 48.0], client.Sample().CcdTemps);
        Assert.Equal(Environment.ProcessPath, client.Details([new(Environment.ProcessId, 1)])!.Single().Path);
        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task A_second_server_cannot_take_the_pipe()
    {
        string name = NewPipe();
        using var stop = new CancellationTokenSource();
        var first = new SensorServer(Fake(1), name).RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        await Task.Delay(300);
        await Assert.ThrowsAnyAsync<IOException>(() => new SensorServer(Fake(2), name).RunAsync(TimeSpan.FromSeconds(30), stop.Token));
        stop.Cancel();
        await first;
    }

    [Fact]
    public void Without_a_helper_the_app_says_so_and_asks_nothing()
    {
        using var client = new SensorClient(null, NewPipe(), helperTask: null, readLocally: false);
        Assert.Null(client.Details([new(1, 1)]));
        Assert.False(client.Full);
    }

    [Fact]
    public async Task An_oversized_request_ends_the_connection_and_the_helper_keeps_serving()
    {
        string name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new SensorServer(Fake(52), name).RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        await Task.Delay(300);
        using (var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut))
        {
            raw.Connect(2000);
            var big = Encoding.UTF8.GetBytes(new string('x', SensorProtocol.MaxLine + 10));
            try
            {
                raw.Write(big, 0, big.Length);
            }
            catch (IOException)
            {
                // the helper hung up mid-write, as it should
            }
        }
        using var client = new SensorClient(null, name, helperTask: null, readLocally: false);
        await WaitFor(() => client.Sample().CpuTemp == 52);
        stop.Cancel();
        await server;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 60; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("timed out");
    }
}
