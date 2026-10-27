using System.Runtime.InteropServices;

namespace Lighthouse.Core.Sensors;

/// <summary>What one process moved per second: disk reads and writes, and network bytes received and sent.</summary>
public sealed record ProcessIo(int Pid, double DiskBps, double NetDownBps, double NetUpBps);

/// <summary>
/// Sums kernel I/O events per process until <see cref="Rates"/> reads them. Pure bookkeeping, so it is tested
/// with recorded payloads. Only sizes and process ids are kept: never addresses, ports or file names.
/// </summary>
public sealed class IoCounter(Func<int, int>? lookupThread = null)
{
    // Classic kernel event classes, told apart by their GUID's first 4 bytes.
    public const int TcpIp = unchecked((int)0x9a280ac0);  // 9a280ac0-c8e0-11d1-84e2-00c04fb998a2
    public const int UdpIp = unchecked((int)0xbf3a50c5);  // bf3a50c5-a9c9-4988-a005-2df0b7c80f80
    public const int DiskIo = 0x3d6fa8d4;                  // 3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c
    public const int Thread = 0x3d6fa8d1;                  // 3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c

    private readonly object _gate = new();
    private Dictionary<int, (long Disk, long Down, long Up)> _bytes = [];
    private readonly Dictionary<int, int> _threadPid = [];
    private DateTime _since = DateTime.UtcNow;

    public long Events { get; private set; }

    /// <summary>One kernel event: its class, its type (opcode), the process in its header and its payload.</summary>
    public void Add(int provider, byte opcode, int headerPid, byte[] payload, int length)
    {
        Events++;
        switch (provider)
        {
            case TcpIp or UdpIp when length >= 8:
                // Send 10 / Recv 11, and their IPv6 twins 26 / 27. The payload starts with the process id and the size.
                bool send = opcode is 10 or 26, receive = opcode is 11 or 27;
                if (!send && !receive) return;
                int netPid = BitConverter.ToInt32(payload, 0), netSize = BitConverter.ToInt32(payload, 4);
                Count(netPid, 0, receive ? netSize : 0, send ? netSize : 0);
                break;
            case DiskIo when length >= 52 && opcode is 10 or 11:
                // Read 10 / Write 11 complete in whatever thread is running, so the issuing thread names the process.
                int diskSize = BitConverter.ToInt32(payload, 8), thread = BitConverter.ToInt32(payload, 48);
                Count(PidOfThread(thread, headerPid), diskSize, 0, 0);
                break;
            case Thread when length >= 8:
                int pid = BitConverter.ToInt32(payload, 0), tid = BitConverter.ToInt32(payload, 4);
                lock (_gate)
                {
                    if (opcode is 1 or 3) _threadPid[tid] = pid;      // start, or already running when tracing began
                    else if (opcode is 2 or 4) _threadPid.Remove(tid); // end
                }
                break;
        }
    }

    private int PidOfThread(int tid, int headerPid)
    {
        lock (_gate)
            if (_threadPid.TryGetValue(tid, out int known)) return known;
        int found = lookupThread?.Invoke(tid) ?? 0;
        if (found > 0)
        {
            lock (_gate) _threadPid[tid] = found;
            return found;
        }
        return headerPid > 0 ? headerPid : 0; // 0: nobody to name, it lands in "Everything else"
    }

    private void Count(int pid, long disk, long down, long up)
    {
        lock (_gate)
        {
            var (d, r, s) = _bytes.GetValueOrDefault(pid);
            _bytes[pid] = (d + disk, r + down, s + up);
        }
    }

    /// <summary>Bytes per second for each process since the last call, and starts counting again.</summary>
    public List<ProcessIo> Rates(DateTime nowUtc)
    {
        Dictionary<int, (long Disk, long Down, long Up)> bytes;
        double seconds;
        lock (_gate)
        {
            (bytes, _bytes) = (_bytes, []);
            seconds = Math.Max(0.001, (nowUtc - _since).TotalSeconds);
            _since = nowUtc;
            if (_threadPid.Count > 200_000) _threadPid.Clear(); // a reused thread id is relearned from the next event
        }
        return bytes.Select(kv => new ProcessIo(kv.Key, kv.Value.Disk / seconds, kv.Value.Down / seconds, kv.Value.Up / seconds)).ToList();
    }
}

/// <summary>
/// A private real-time kernel trace (Windows 8+ lets a session other than the NT Kernel Logger receive kernel
/// events) of network sends and receives, disk reads and writes, and thread starts and ends. It needs admin, so
/// only the sensor helper runs it.
/// </summary>
public sealed class IoTracer : IDisposable
{
    public const string SessionName = "Lighthouse-IO";
    private const uint EVENT_TRACE_FLAG_THREAD = 0x2, EVENT_TRACE_FLAG_DISK_IO = 0x100, EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x10000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x100, EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint ERROR_ALREADY_EXISTS = 183, EVENT_TRACE_CONTROL_STOP = 1;
    private const int PropertiesSize = 120;

    private readonly EventRecordCallback _callback;
    private readonly byte[] _payload = new byte[512];
    private ulong _trace = ulong.MaxValue;
    private Thread? _thread;

    public IoCounter Counter { get; } = new(ThreadOwner);
    public string? Error { get; private set; }

    public IoTracer() => _callback = OnEvent;

    public bool Start()
    {
        Stop(); // a session left over from a helper that didn't close it
        uint flags = EVENT_TRACE_FLAG_THREAD | EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_NETWORK_TCPIP;
        IntPtr properties = Properties(EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE, flags);
        try
        {
            uint rc = StartTraceW(out _, SessionName, properties);
            if (rc == ERROR_ALREADY_EXISTS)
            {
                Stop();
                rc = StartTraceW(out _, SessionName, properties);
            }
            if (rc != 0)
            {
                Error = rc == 5 ? "kernel tracing needs admin" : $"kernel tracing could not start (error {rc})";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }

        var logfile = new EVENT_TRACE_LOGFILE
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            CurrentEvent = new byte[88],
            LogfileHeader = new byte[280],
        };
        _trace = OpenTraceW(ref logfile);
        Marshal.FreeHGlobal(logfile.LoggerName);
        if (_trace == ulong.MaxValue)
        {
            Error = $"kernel tracing could not be read (error {Marshal.GetLastWin32Error()})";
            Stop();
            return false;
        }
        _thread = new Thread(() =>
        {
            uint rc = ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero);
            if (rc != 0 && Error is null) Error = $"kernel tracing stopped (error {rc})";
        }) { IsBackground = true, Name = "Lighthouse I/O trace" };
        _thread.Start();
        return true;
    }

    /// <summary>Called by ETW on the trace thread for every event.</summary>
    private void OnEvent(IntPtr record)
    {
        // EVENT_RECORD: header ProcessId at 12, ProviderId at 24, Opcode at 45; UserDataLength at 86, UserData at 96.
        int provider = Marshal.ReadInt32(record, 24);
        if (provider is not (IoCounter.TcpIp or IoCounter.UdpIp or IoCounter.DiskIo or IoCounter.Thread)) return;
        int length = Math.Min((ushort)Marshal.ReadInt16(record, 86), _payload.Length);
        IntPtr data = Marshal.ReadIntPtr(record, 96);
        if (data == IntPtr.Zero || length == 0) return;
        Marshal.Copy(data, _payload, 0, length);
        Counter.Add(provider, Marshal.ReadByte(record, 45), Marshal.ReadInt32(record, 12), _payload, length);
    }

    /// <summary>Stops the session by name (also one a previous helper left running).</summary>
    private static void Stop()
    {
        IntPtr properties = Properties(0, 0, nameRoom: 1024);
        try
        {
            ControlTraceW(0, SessionName, properties, EVENT_TRACE_CONTROL_STOP);
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64) followed by the session name.</summary>
    private static IntPtr Properties(uint mode, uint enableFlags, int nameRoom = 0)
    {
        int nameBytes = (Math.Max(SessionName.Length, nameRoom) + 1) * 2;
        int size = PropertiesSize + nameBytes;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                                 // Wnode.BufferSize
        Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, p + 24, 16);      // Wnode.Guid: this session's own id
        Marshal.WriteInt32(p, 40, 1);                                   // Wnode.ClientContext: QPC timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);                          // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);                                  // BufferSize, KB
        Marshal.WriteInt32(p, 52, 4);                                   // MinimumBuffers
        Marshal.WriteInt32(p, 56, 32);                                  // MaximumBuffers
        Marshal.WriteInt32(p, 64, (int)mode);                           // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                   // FlushTimer, s
        Marshal.WriteInt32(p, 72, (int)enableFlags);                    // EnableFlags
        Marshal.WriteInt32(p, 116, PropertiesSize);                     // LoggerNameOffset
        return p;
    }

    /// <summary>The process a thread belongs to, for disk events whose issuing thread was running before tracing began.</summary>
    private static int ThreadOwner(int tid)
    {
        IntPtr thread = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, tid);
        if (thread == IntPtr.Zero) return 0;
        try
        {
            return (int)GetProcessIdOfThread(thread);
        }
        finally
        {
            Native.NativeMethods.CloseHandle(thread);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_trace != ulong.MaxValue) CloseTrace(_trace);
        _trace = ulong.MaxValue;
        _thread?.Join(2000);
    }

    private delegate void EventRecordCallback(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_LOGFILE
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)] public byte[] CurrentEvent;   // EVENT_TRACE
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)] public byte[] LogfileHeader; // TRACE_LOGFILE_HEADER
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint control);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);
}
