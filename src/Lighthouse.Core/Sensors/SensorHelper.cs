using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Lighthouse.Core.Autostart;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Sensors;

// Lighthouse runs as two processes. The app (tray icon, flyout, history, settings) runs with the user's own rights.
// The sensor helper (`Lighthouse.exe --sensors`, started by its own elevated logon task) is the only part with admin
// rights, because the PawnIO driver behind CPU, RAM and SSD sensors needs them. It has no window and writes no files;
// it answers a few questions on a named pipe only the user's account can open. Any program running as the user can
// ask the same questions, so each answer is something that may be told to such a program.

/// <summary>A process as both sides know it: its pid and when it started (pids get reused).</summary>
public sealed record ProcessKey(int Pid, long CreateTime);

/// <summary>A process's image path; for a local LLM server also its command line and account.</summary>
public sealed record ProcessDetail(int Pid, long CreateTime, string? Path, string? CommandLine = null, string? Owner = null);

public sealed record SensorRequest
{
    public string Op { get; init; } = "";
    public List<ProcessKey>? Processes { get; init; }
}

public sealed record SensorResponse
{
    public int Version { get; init; } = SensorProtocol.Version;
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public HardwareReading? Reading { get; init; }
    /// <summary>The helper is admin, so its readings include CPU, RAM and SSD sensors.</summary>
    public bool Elevated { get; init; }
    public List<ProcessDetail>? Details { get; init; }
    /// <summary>Bytes per second each process moved (disk, network), from kernel tracing; null when it isn't running.</summary>
    public List<ProcessIo>? Io { get; init; }
    public string? IoError { get; init; }
    /// <summary>Things to tell the user, such as the startup guard switching an entry off.</summary>
    public List<string>? Notices { get; init; }
    /// <summary>The helper's log lines: it writes no files, so the app logs them.</summary>
    public List<string>? Log { get; init; }
}

public static class SensorProtocol
{
    public const int Version = 1;
    public const int MaxLine = 1 << 20;
    public const int MaxProcesses = 4096;

    /// <summary>One pipe per Windows session, so two signed-in users each get their own helper.</summary>
    public static string PipeName { get; } = $"Lighthouse.Sensors.{Process.GetCurrentProcess().SessionId}";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>The helper's answers: everything a program running as the user may ask, and nothing more.</summary>
public sealed class SensorHandler(
    Func<HardwareReading> reading,
    Func<string?> error,
    Func<(List<ProcessIo>? Io, string? Error)> io,
    Func<IReadOnlyList<ProcInfo>> processes,
    Func<int, (string? Path, string? CommandLine)> details,
    Func<int, string?> owner,
    Action<int> kill,
    Func<List<string>> notices,
    Func<List<string>> log)
{
    public const string LlmServerExe = "llama-server.exe";

    public SensorResponse Handle(SensorRequest request) => request.Op switch
    {
        "hello" => new SensorResponse { Ok = true },
        "reading" => Reading(),
        "details" => new SensorResponse { Ok = true, Details = Details(request.Processes ?? []) },
        "stop-llm" => StopLlm(request.Processes is [var key, ..] ? key : null),
        _ => new SensorResponse { Error = "unknown request" },
    };

    private SensorResponse Reading()
    {
        var (rates, ioError) = io();
        return new SensorResponse
        {
            Ok = true, Reading = reading(), Error = error(), Elevated = UserContext.Elevated, Io = rates, IoError = ioError,
            Notices = notices(), Log = log(),
        };
    }

    /// <summary>
    /// Paths for any process the app can't open itself (another account's, a service), which is how it names them.
    /// Command lines and accounts only for the local LLM server: the app needs its port, key file and model, and
    /// other processes' command lines can carry other people's secrets.
    /// </summary>
    private List<ProcessDetail> Details(List<ProcessKey> keys)
    {
        var live = processes().ToDictionary(p => (p.Pid, p.CreateTime));
        var result = new List<ProcessDetail>();
        foreach (var key in keys.Take(SensorProtocol.MaxProcesses))
        {
            if (!live.TryGetValue((key.Pid, key.CreateTime), out var proc)) continue; // gone, or the pid was reused
            var (path, commandLine) = details(key.Pid);
            bool llm = proc.Name.Equals(LlmServerExe, StringComparison.OrdinalIgnoreCase);
            result.Add(new ProcessDetail(key.Pid, key.CreateTime, path, llm ? commandLine : null, llm ? owner(key.Pid) : null));
        }
        return result;
    }

    /// <summary>Stops a running llama-server.exe (the flyout's "Stop local LLM" for a game). Nothing else can be stopped.</summary>
    private SensorResponse StopLlm(ProcessKey? key)
    {
        var proc = key is null ? null : processes().FirstOrDefault(p => p.Pid == key.Pid && p.CreateTime == key.CreateTime);
        if (proc is null || !proc.Name.Equals(LlmServerExe, StringComparison.OrdinalIgnoreCase))
            return new SensorResponse { Error = "that is not a running llama-server.exe" };
        try
        {
            kill(proc.Pid);
            return new SensorResponse { Ok = true };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return new SensorResponse { Error = e.Message };
        }
    }
}

/// <summary>The helper's end of the pipe: only the user's account may open it, never over the network.</summary>
public sealed class SensorServer(SensorHandler handler, string? pipeName = null)
{
    private const int MaxClients = 4;
    private readonly string _pipeName = pipeName ?? SensorProtocol.PipeName;
    private int _clients;
    private long _lastClientTicks = DateTime.UtcNow.Ticks;
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _open = new();

    /// <summary>
    /// Serves until no client has been connected for <paramref name="idle"/> (the app went away). Throws IOException
    /// when another process already has the pipe, so a stand-in can't sit in front of the helper unnoticed.
    /// </summary>
    public async Task RunAsync(TimeSpan idle, CancellationToken stop, TimeSpan? firstClientWait = null)
    {
        // At logon the app starts a few seconds after the helper, later on a slow desktop: give it longer the first time.
        Interlocked.Exchange(ref _lastClientTicks, DateTime.UtcNow.Add((firstClientWait ?? idle) - idle).Ticks);
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var watchdog = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (Volatile.Read(ref _clients) == 0 && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastClientTicks)) > idle)
                    done.Cancel();
            }
        });
        var sessions = new List<Task>();
        bool first = true;
        while (!done.IsCancellationRequested)
        {
            var pipe = Create(first);
            first = false;
            try
            {
                await pipe.WaitForConnectionAsync(done.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            Interlocked.Increment(ref _clients);
            sessions.Add(Task.Run(() => Serve(pipe)));
            sessions.RemoveAll(t => t.IsCompleted);
            while (Volatile.Read(ref _clients) >= MaxClients && !done.IsCancellationRequested) await Task.Delay(100).ConfigureAwait(false);
        }
        // Close the sessions still open (a client that stays connected would otherwise keep a read waiting forever).
        foreach (var open in _open.Keys) open.Dispose();
        await Task.WhenAll(sessions).ConfigureAwait(false);
        await watchdog.ConfigureAwait(false);
    }

    private NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options, 0, 0, security);
        }
        catch (UnauthorizedAccessException e) when (first)
        {
            // FirstPipeInstance: Windows refuses the name when any process already has it.
            throw new IOException($"another process already has the pipe {_pipeName}", e);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        _open[pipe] = 0;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            while (pipe.IsConnected)
            {
                string? line = await ReadLine(reader).ConfigureAwait(false);
                if (line is null) break;
                SensorResponse response;
                try
                {
                    response = handler.Handle(JsonSerializer.Deserialize<SensorRequest>(line, SensorProtocol.Json) ?? new SensorRequest());
                }
                catch (JsonException)
                {
                    response = new SensorResponse { Error = "not a request" };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, SensorProtocol.Json)).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The client went away mid-answer, or the helper is shutting down.
        }
        finally
        {
            _open.TryRemove(pipe, out _);
            pipe.Dispose();
            Interlocked.Exchange(ref _lastClientTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref _clients);
        }
    }

    /// <summary>One request line, refused past <see cref="SensorProtocol.MaxLine"/> characters.</summary>
    private static async Task<string?> ReadLine(StreamReader reader)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) return line.Length > 0 ? line.ToString() : null;
            if (one[0] == '\n') return line.ToString();
            if (line.Length >= SensorProtocol.MaxLine) throw new IOException("request too long");
            line.Append(one[0]);
        }
    }
}

/// <summary>Where the app gets hardware readings: the sensor helper, or this process itself.</summary>
public interface IHardwareSource : IDisposable
{
    HardwareReading Sample();
    string? Error { get; }
    /// <summary>True when CPU, RAM and SSD sensors can be read: the helper answers, or this process is admin itself.</summary>
    bool Full { get; }
}

/// <summary>This process reads the sensors itself: everything when it is admin, the GPU's alone when it isn't.</summary>
public sealed class LocalHardware(string? gpuName) : IHardwareSource
{
    private readonly HardwareSampler _sampler = new(gpuName);
    public HardwareReading Sample() => _sampler.Sample();
    public string? Error => _sampler.Error;
    public bool Full => HardwareSampler.IsElevated && _sampler.Error is null;
    public void Dispose() => _sampler.Dispose();
}

/// <summary>
/// The app's end of the pipe. It only talks to a helper running this same Lighthouse.exe (checked by the pipe's
/// server process), starts the helper's task when there is none, and falls back to the sensors this process can
/// read itself while there is no helper.
/// </summary>
public sealed class SensorClient(string? gpuName, string? pipeName = null, string? helperTask = @"\Lighthouse\Sensors",
    bool readLocally = true) : IHardwareSource
{
    private readonly string _pipeName = pipeName ?? SensorProtocol.PipeName;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _notices = new(), _log = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private DateTime _nextConnect = DateTime.MinValue, _nextStart = DateTime.MinValue;
    private LocalHardware? _fallback;
    private string? _refusal;

    public string? Error { get; private set; }
    /// <summary>The helper's latest per-process disk and network rates; empty without its kernel tracing.</summary>
    public IReadOnlyDictionary<int, ProcessIo> Io { get; private set; } = new Dictionary<int, ProcessIo>();
    public string? IoError { get; private set; } = "the sensor helper isn't running";
    public bool Connected { get { lock (_gate) return _pipe is { IsConnected: true }; } }
    /// <summary>A helper answers, and it is admin (a helper started without admin can't read CPU sensors either).</summary>
    public bool Full => Connected && _helperElevated;
    private bool _helperElevated;

    public HardwareReading Sample()
    {
        if (Call(new SensorRequest { Op = "reading" }) is { Ok: true } r)
        {
            Error = r.Error ?? (r.Elevated ? null : "the sensor helper isn't running as admin");
            _helperElevated = r.Elevated;
            Io = (r.Io ?? []).ToDictionary(p => p.Pid);
            IoError = r.Io is null ? r.IoError ?? "per-program disk and network aren't available" : null;
            foreach (string n in r.Notices ?? []) _notices.Enqueue(n);
            foreach (string l in r.Log ?? []) _log.Enqueue(l);
            return r.Reading ?? new HardwareReading();
        }
        // No helper: what this process can read with the user's own rights (the GPU's sensors).
        Error = _refusal ?? "the sensor helper isn't running";
        Io = new Dictionary<int, ProcessIo>();
        IoError = Error;
        if (!readLocally) return new HardwareReading();
        _fallback ??= new LocalHardware(gpuName);
        return _fallback.Sample();
    }

    /// <summary>What the helper says about processes this one can't open; null while there is no helper.</summary>
    public IReadOnlyList<ProcessDetail>? Details(IReadOnlyList<ProcessKey> keys) =>
        keys.Count == 0 ? [] : Call(new SensorRequest { Op = "details", Processes = keys.ToList() }) is { Ok: true } r ? r.Details ?? [] : null;

    public bool StopLlm(ProcessKey key) => Call(new SensorRequest { Op = "stop-llm", Processes = [key] }) is { Ok: true };

    public List<string> DrainNotices() => Drain(_notices);
    public List<string> DrainLog() => Drain(_log);

    private static List<string> Drain(ConcurrentQueue<string> queue)
    {
        var list = new List<string>();
        while (queue.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    private SensorResponse? Call(SensorRequest request)
    {
        lock (_gate)
        {
            if (!EnsureConnected()) return null;
            try
            {
                _writer!.WriteLine(JsonSerializer.Serialize(request, SensorProtocol.Json));
                var read = _reader!.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("the sensor helper did not answer");
                return read.Result is { } line ? JsonSerializer.Deserialize<SensorResponse>(line, SensorProtocol.Json) : Drop();
            }
            catch (Exception e) when (e is IOException or TimeoutException or JsonException or AggregateException or ObjectDisposedException
                                          or InvalidOperationException)
            {
                return Drop();
            }
        }
    }

    private SensorResponse? Drop()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        (_pipe, _reader, _writer) = (null, null, null);
        return null;
    }

    private bool EnsureConnected()
    {
        if (_pipe is { IsConnected: true }) return true;
        Drop();
        if (DateTime.Now < _nextConnect) return false;
        _nextConnect = DateTime.Now.AddSeconds(5);
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(200);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            StartHelper();
            return false;
        }
        string? image = ServerImage(pipe);
        if (image is null || !string.Equals(image, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            // Some other program holds the helper's pipe name: never take readings from it.
            _refusal = $"the sensor pipe is held by {image ?? "an unknown process"}, not Lighthouse's sensor helper";
            Log.Write(_refusal);
            pipe.Dispose();
            return false;
        }
        _refusal = null;
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        return true;
    }

    /// <summary>Starts the helper's logon task (the user may run it, not change it), at most once a minute.</summary>
    private void StartHelper()
    {
        if (helperTask is null || DateTime.Now < _nextStart) return;
        _nextStart = DateTime.Now.AddMinutes(1);
        try
        {
            int slash = helperTask.LastIndexOf('\\');
            ScheduledTasks.Run(helperTask[..(slash + 1)], helperTask[(slash + 1)..]);
        }
        catch (Exception e)
        {
            Log.Write($"could not start the sensor helper: {e.Message}");
        }
    }

    /// <summary>The full path of the process serving the pipe.</summary>
    private static string? ServerImage(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return null;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int length = name.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public void Dispose()
    {
        lock (_gate) Drop();
        _fallback?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}

/// <summary>
/// The `--sensors` process: reads the driver's sensors once a second, keeps the saved startup list off, answers on
/// the pipe, and exits 30 s after the app goes away. No window, no files: its log lines go to the app.
/// </summary>
public static class SensorHost
{
    public static int Run(CancellationToken stop)
    {
        var log = new ConcurrentQueue<string>();
        Log.Redirect = line =>
        {
            log.Enqueue(line);
            while (log.Count > 200) log.TryDequeue(out _);
        };
        var notices = new ConcurrentQueue<string>();
        var gpu = Dxgi.MainAdapter();
        using var sampler = new HardwareSampler(gpu?.Name);
        if (sampler.Error is { } error) Log.Write($"sensors unavailable: {error}");
        HardwareReading latest = new();
        var guard = new AutostartGuard(Paths.GuardOffFile, new WindowsAutostartBackend());
        // Per-process disk and network from a kernel trace. Without it the app falls back to I/O counters for disk.
        var tracer = new IoTracer();
        bool tracing = tracer.Start();
        Log.Write(tracing ? "per-program disk and network: kernel tracing on" : $"per-program disk and network unavailable: {tracer.Error}");
        List<ProcessIo>? latestIo = null;

        using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sampling = Task.Run(async () =>
        {
            var nextGuard = DateTime.Now.AddSeconds(30);
            while (!done.IsCancellationRequested)
            {
                try
                {
                    Volatile.Write(ref latest, sampler.Sample());
                    if (tracing) Volatile.Write(ref latestIo, tracer.Counter.Rates(DateTime.UtcNow).Where(p => p.DiskBps + p.NetDownBps + p.NetUpBps > 0).ToList());
                    if (DateTime.Now >= nextGuard)
                    {
                        nextGuard = DateTime.Now.AddMinutes(10);
                        foreach (var entry in guard.Enforce(AutostartList.Read()))
                            notices.Enqueue($"{entry} was on; Lighthouse switched it off again.");
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"sensor sample failed: {e.Message}");
                }
                try
                {
                    await Task.Delay(1000, done.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        static List<string> Drain(ConcurrentQueue<string> queue)
        {
            var list = new List<string>();
            while (queue.TryDequeue(out var item)) list.Add(item);
            return list;
        }

        var handler = new SensorHandler(() => Volatile.Read(ref latest), () => sampler.Error,
            () => (Volatile.Read(ref latestIo), tracing ? tracer.Error : tracer.Error ?? "kernel tracing is off"), ProcessTable.Read, ProcessTable.Details,
            ProcessTable.Owner, Kill, () => Drain(notices), () => Drain(log));
        int exitCode = 0;
        try
        {
            new SensorServer(handler).RunAsync(TimeSpan.FromSeconds(30), done.Token, TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            exitCode = 3; // another process has the pipe (usually a helper that is already running)
        }
        done.Cancel();
        sampling.Wait(TimeSpan.FromSeconds(3));
        tracer.Dispose();
        return exitCode;
    }

    private static void Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
        process.WaitForExit(10_000);
    }
}
