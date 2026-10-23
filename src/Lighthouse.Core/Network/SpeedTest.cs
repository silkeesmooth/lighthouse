using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Network;

/// <summary>The adapter the internet traffic leaves through: "Wi-Fi" or "Ethernet", its name, its link rate.</summary>
public sealed record ConnectionInfo(string Kind, string Adapter, double? LinkMbps)
{
    /// <summary>The adapter Windows routes 1.1.1.1 through, or null when it can't tell.</summary>
    public static ConnectionInfo? Current()
    {
        try
        {
            uint dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0); // network byte order
            if (NativeMethods.GetBestInterface(dest, out uint index) != 0) return null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                int? nicIndex;
                try
                {
                    nicIndex = nic.GetIPProperties().GetIPv4Properties()?.Index;
                }
                catch (NetworkInformationException)
                {
                    continue; // no IPv4 on this one
                }
                if (nicIndex != index) continue;
                string kind = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                        or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                    var other => other.ToString(),
                };
                return new ConnectionInfo(kind, nic.Description, nic.Speed > 0 ? nic.Speed / 1e6 : null);
            }
        }
        catch (Exception e) when (e is NetworkInformationException or FormatException)
        {
            // Unknown is fine: the speeds still stand.
        }
        return null;
    }
}

public sealed record SpeedResult(double PingMs, double DownMbps, double UpMbps, long BytesUsed, ConnectionInfo? Connection)
{
    /// <summary>What the machine was already moving in the seconds before the test, in Mbps (null: not known).</summary>
    public double? BackgroundDownMbps { get; init; }
    public double? BackgroundUpMbps { get; init; }
    /// <summary>The program behind most of that traffic, when per-program network is known.</summary>
    public string? BackgroundBy { get; init; }

    public string Summary => $"{SpeedTest.Format(DownMbps)} down, {SpeedTest.Format(UpMbps)} up, {PingMs:0} ms ping.";

    /// <summary>The line under the numbers: "Ethernet · Realtek PCIe 2.5GbE Family Controller · 1000 Mbps link · 38 MB used".</summary>
    public string ConnectionLine => string.Join(" · ", new[]
    {
        Connection?.Kind, Connection?.Adapter, Connection?.LinkMbps is { } link ? $"{link:0} Mbps link" : null, $"{BytesUsed / 1e6:0} MB used",
    }.Where(p => p is not null));

    public string Detail
    {
        get
        {
            string over = Connection is { } c
                ? $"Over {c.Kind} ({c.Adapter}" + (c.LinkMbps is { } link ? $", link {link:0} Mbps)." : ").")
                : "";
            return $"{over} Used {BytesUsed / 1e6:0} MB of data.{BusyNote}".Trim();
        }
    }

    /// <summary>
    /// Other traffic shares the line, so the test only gets what is left. 2026-10-22: a model download at 34 Mbps made
    /// a ~43 Mbps line test at 9 Mbps down with a 110 ms ping.
    /// </summary>
    public string BusyNote
    {
        get
        {
            static bool Busy(double? background, double measured) => background is { } b && b >= 2 && b >= 0.2 * measured;
            bool down = Busy(BackgroundDownMbps, DownMbps), up = Busy(BackgroundUpMbps, UpMbps);
            if (!down && !up) return "";
            string what = down && up
                ? $"downloading {SpeedTest.Format(BackgroundDownMbps!.Value)} and uploading {SpeedTest.Format(BackgroundUpMbps!.Value)}"
                : down ? $"downloading {SpeedTest.Format(BackgroundDownMbps!.Value)}" : $"uploading {SpeedTest.Format(BackgroundUpMbps!.Value)}";
            string by = BackgroundBy is { } program ? $" (mostly {program})" : "";
            return $" Something else on this PC was already {what} when the test started{by}, so the connection can do more than this.";
        }
    }
}

/// <summary>What the test is doing now: "Download" at 312 Mbps so far; once ping is measured, <see cref="PingMs"/>.</summary>
public sealed record SpeedProgress(string Phase, double? Mbps, double? PingMs = null);

/// <summary>A finished test, or why there is none.</summary>
public sealed record SpeedOutcome(SpeedResult? Result, string? Error);

/// <summary>
/// An internet speed test against Cloudflare's public speed-test endpoints (the ones speed.cloudflare.com uses; no
/// account or key). Ping is the median of five small requests; download and upload each run four parallel streams
/// and skip the first second, while TCP is still speeding up. Capped by time and by data: at most
/// <see cref="MaxDownBytes"/> down and <see cref="MaxUpBytes"/> up.
/// </summary>
public static class SpeedTest
{
    public const string Endpoint = "https://speed.cloudflare.com";
    public const long MaxDownBytes = 500_000_000;
    public const long MaxUpBytes = 200_000_000;
    private const int Streams = 4;
    private static readonly TimeSpan DownTime = TimeSpan.FromSeconds(8), UpTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { MaxConnectionsPerServer = Streams + 1, ConnectCallback = ConnectAsync })
    {
        Timeout = Timeout.InfiniteTimeSpan, // each phase stops itself
    };

    private static readonly TimeSpan PerAddress = TimeSpan.FromSeconds(5);

    /// <summary>Set by `--speed-test` to see where the time goes (DNS, each connect, the first answer). Null: silent.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// IPv4 first, 5 s per address, then the next. Many home networks hand out IPv6 addresses that can't reach the
    /// internet (a LAN that hands out only a local fdxx:: address, with no IPv6 route), and .NET's own connect tries each address
    /// with no limit: a test sat on "Measuring ping" for 40 s and then failed with "unreachable network".
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        Trace?.Invoke($"dns {context.DnsEndPoint.Host}: {string.Join(", ", addresses.Select(a => a.ToString()))} in {sw.ElapsedMilliseconds} ms");
        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream,
                ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(PerAddress);
                sw.Restart();
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, limit.Token);
                Trace?.Invoke($"connect {address}: {sw.ElapsedMilliseconds} ms");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when ((e is SocketException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                Trace?.Invoke($"connect {address}: failed after {sw.ElapsedMilliseconds} ms ({e.GetType().Name})");
                last = e is OperationCanceledException ? new TimeoutException($"{address} did not answer within {PerAddress.TotalSeconds:0} s") : e;
            }
        }
        throw new HttpRequestException($"Could not connect to {context.DnsEndPoint.Host}: {last?.Message ?? "no addresses"}", last);
    }

    public static async Task<SpeedResult> RunAsync(IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        var connection = ConnectionInfo.Current();
        progress?.Report(new("Ping", null));
        double ping = await PingAsync(ct);
        progress?.Report(new("Ping", null, ping));
        var (down, downBytes) = await MeasureAsync("Download", DownloadAsync, DownTime, MaxDownBytes, progress, ct);
        var (up, upBytes) = await MeasureAsync("Upload", UploadAsync, UpTime, MaxUpBytes, progress, ct);
        return new SpeedResult(ping, down, up, downBytes + upBytes, connection);
    }

    /// <summary>"412 Mbps", "1.2 Gbps", "8.4 Mbps".</summary>
    public static string Format(double mbps) => Parts(mbps) is var (value, unit) ? $"{value} {unit}" : "";

    /// <summary>The number and its unit apart, for big-number displays: ("412", "Mbps"), ("1.2", "Gbps").</summary>
    public static (string Value, string Unit) Parts(double mbps) =>
        mbps >= 1000 ? ($"{mbps / 1000:0.0}", "Gbps") : mbps >= 10 ? ($"{mbps:0}", "Mbps") : ($"{mbps:0.0}", "Mbps");

    /// <summary>Megabits a second from bytes over a span, 0 for an empty span.</summary>
    public static double Mbps(long bytes, TimeSpan span) => span.TotalSeconds <= 0 ? 0 : bytes * 8 / span.TotalSeconds / 1e6;

    private static async Task<double> PingAsync(CancellationToken ct)
    {
        var times = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await Http.GetAsync($"{Endpoint}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (i == 0) Trace?.Invoke($"first answer (proxy lookup, connect, TLS, request): {sw.ElapsedMilliseconds} ms");
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds); // the first one pays for the connection and TLS
        }
        times.Sort();
        return times[times.Count / 2];
    }

    private sealed class Counter
    {
        public long Bytes;
    }

    /// <summary>Runs the streams until the time or data cap, and returns the speed after the warm-up and the bytes used.</summary>
    private static async Task<(double Mbps, long Bytes)> MeasureAsync(string phase, Func<Counter, CancellationToken, Task> stream,
        TimeSpan duration, long maxBytes, IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        var counter = new Counter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Streams).Select(_ => stream(counter, stop.Token)).ToList();
        TimeSpan? warmAt = null;
        long warmBytes = 0;
        progress?.Report(new(phase, null));
        while (sw.Elapsed < duration && Interlocked.Read(ref counter.Bytes) < maxBytes && !workers.All(w => w.IsCompleted))
        {
            await Task.Delay(250, ct);
            long bytes = Interlocked.Read(ref counter.Bytes);
            if (warmAt is null && sw.Elapsed >= Warmup)
            {
                warmAt = sw.Elapsed;
                warmBytes = bytes;
            }
            else if (warmAt is { } from)
            {
                progress?.Report(new(phase, Mbps(bytes - warmBytes, sw.Elapsed - from)));
            }
        }
        var elapsed = sw.Elapsed;
        long total = Interlocked.Read(ref counter.Bytes);
        stop.Cancel();
        try
        {
            await Task.WhenAll(workers);
        }
        catch when (!ct.IsCancellationRequested && total > 0)
        {
            // Streams stop by cancellation (or by the server closing early); what they moved still counts.
        }
        if (total == 0) throw new HttpRequestException($"{phase} moved no data.");
        double mbps = warmAt is { } w && elapsed - w >= TimeSpan.FromSeconds(1)
            ? Mbps(total - warmBytes, elapsed - w)
            : Mbps(total, elapsed); // a very fast line hits the data cap before the warm-up is over
        return (mbps, total);
    }

    private static async Task DownloadAsync(Counter counter, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (!ct.IsCancellationRequested)
        {
            using var response = await Http.GetAsync($"{Endpoint}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0) Interlocked.Add(ref counter.Bytes, n);
        }
    }

    private static async Task UploadAsync(Counter counter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var content = new CountingContent(10_000_000, counter);
            using var response = await Http.PostAsync($"{Endpoint}/__up", content, ct);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Random bytes, counted as they are written to the connection.</summary>
    private sealed class CountingContent(long length, Counter counter) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var chunk = new byte[65536];
            Random.Shared.NextBytes(chunk);
            for (long left = length; left > 0;)
            {
                int n = (int)Math.Min(chunk.Length, left);
                await stream.WriteAsync(chunk.AsMemory(0, n), ct);
                Interlocked.Add(ref counter.Bytes, n);
                left -= n;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length2)
        {
            length2 = length;
            return true;
        }
    }
}
