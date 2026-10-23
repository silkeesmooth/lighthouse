using Lighthouse.Core.Model;
using Lighthouse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Lighthouse.Core.History;

public sealed record MinuteRow(DateTime Time, double Cpu, double Gpu, double RamGb, double VramGb, double? CpuTemp,
    double? GpuTemp, double? GpuHotspot, double? CpuPower, double? GpuPower, string? Top);

public sealed record EventRow(DateTime Time, string Kind, string Title, string Detail);

/// <summary>One finished run of a watched job. <see cref="Kind"/> is its command ("load"), so each is timed apart.</summary>
public sealed record JobRun(string Job, string Kind, string Label, DateTime Start, DateTime End, int? Exit)
{
    public TimeSpan Duration => End - Start;
    public bool Succeeded => Exit == 0;
}

/// <summary>One speed test, with what other traffic was already on the line (Mbps, both ways), if known.</summary>
public sealed record SpeedRun(DateTime Time, double DownMbps, double UpMbps, double PingMs, string? Connection, double? BackgroundMbps);

/// <summary>
/// Minute averages (90 days), events (1 year) and game sessions, in %LOCALAPPDATA%\Lighthouse\history.db.
/// Pass <see cref="InMemory"/> for a throwaway store: diagnostic runs must never touch the real file.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const string InMemory = ":memory:";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public HistoryStore(string path)
    {
        _db = Open(path);
        CreateTables();
    }

    /// <summary>
    /// Opens the database. A file that fails SQLite's own check is set aside (history.corrupt-*.db) and a fresh
    /// one started, so a damaged file costs the old history rather than every future minute.
    /// </summary>
    private static SqliteConnection Open(string path)
    {
        if (path == InMemory) return OpenConnection(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (new[] { path, path + "-wal", path + "-shm" }.Any(Paths.IsLink))
        {
            // SQLite would write through a link, as admin, wherever it points.
            Log.Write("history.db (or its -wal or -shm file) is a link, so history stays in memory this run");
            return OpenConnection(InMemory);
        }
        var db = OpenConnection(path);
        string check;
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "pragma quick_check";
            check = cmd.ExecuteScalar() as string ?? "no answer";
        }
        catch (SqliteException e)
        {
            check = e.Message;
        }
        if (check == "ok") return db;

        db.Dispose();
        SqliteConnection.ClearAllPools();
        string aside = Path.Combine(Path.GetDirectoryName(path)!, $"history.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        File.Move(path, aside);
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix);
        Log.Write($"history.db failed its check ({check}); moved to {Path.GetFileName(aside)} and started fresh");
        return OpenConnection(path);
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        return db;
    }

    private void CreateTables()
    {
        Exec("""
            pragma journal_mode = wal;
            create table if not exists minutes (
                ts integer primary key, cpu real, gpu real, ram real, vram real, cpu_temp real, gpu_temp real,
                gpu_hotspot real, cpu_power real, gpu_power real, top text);
            create table if not exists events (
                id integer primary key autoincrement, ts integer not null, kind text not null, title text not null, detail text);
            create index if not exists events_ts on events(ts);
            create table if not exists games (
                id integer primary key autoincrement, start integer not null, end integer not null, game text not null,
                gpu_avg real, gpu_temp_max real, hotspot_max real, cpu_temp_max real, parked_pct real, vram_peak real);
            create table if not exists job_runs (
                id integer primary key autoincrement, start integer not null, end integer not null, job text not null,
                kind text not null, label text not null, exit integer);
            create index if not exists job_runs_job on job_runs(job, kind, end);
            create table if not exists speed_tests (
                ts integer primary key, down real not null, up real not null, ping real not null, connection text, background real);
            """);
    }

    public void AddMinute(MinuteRow m) => Exec(
        "insert or replace into minutes values ($ts, $cpu, $gpu, $ram, $vram, $ct, $gt, $gh, $cp, $gp, $top)",
        ("$ts", Unix(m.Time)), ("$cpu", m.Cpu), ("$gpu", m.Gpu), ("$ram", m.RamGb), ("$vram", m.VramGb), ("$ct", m.CpuTemp),
        ("$gt", m.GpuTemp), ("$gh", m.GpuHotspot), ("$cp", m.CpuPower), ("$gp", m.GpuPower), ("$top", m.Top));

    public void AddEvent(string kind, string title, string detail) => Exec(
        "insert into events (ts, kind, title, detail) values ($ts, $k, $t, $d)",
        ("$ts", Unix(DateTime.Now)), ("$k", kind), ("$t", title), ("$d", detail));

    public void AddGame(GameSession g) => Exec(
        "insert into games (start, end, game, gpu_avg, gpu_temp_max, hotspot_max, cpu_temp_max, parked_pct, vram_peak) " +
        "values ($s, $e, $g, $ga, $gt, $h, $c, $p, $v)",
        ("$s", Unix(g.Start)), ("$e", Unix(g.End)), ("$g", g.Game), ("$ga", g.GpuAvg), ("$gt", g.GpuTempMax),
        ("$h", g.HotspotMax), ("$c", g.CpuTempMax), ("$p", g.ParkedPct), ("$v", g.VramPeakGb));

    public void AddJobRun(JobRun r) => Exec(
        "insert into job_runs (start, end, job, kind, label, exit) values ($s, $e, $j, $k, $l, $x)",
        ("$s", Unix(r.Start)), ("$e", Unix(r.End)), ("$j", r.Job), ("$k", r.Kind), ("$l", r.Label), ("$x", r.Exit));

    /// <summary>Finished runs, newest first: of one job and kind, of one job, or of every job.</summary>
    public List<JobRun> JobRuns(string? job = null, string? kind = null, int limit = 50, DateTime? since = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select job, kind, label, start, end, exit from job_runs where ($j is null or job = $j) " +
                              "and ($k is null or kind = $k) and end >= $since order by end desc limit $n";
            cmd.Parameters.AddWithValue("$j", (object?)job ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", (object?)kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$since", Unix(since ?? DateTime.UnixEpoch.ToLocalTime()));
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var rows = new List<JobRun>();
            while (r.Read())
                rows.Add(new JobRun(r.GetString(0), r.GetString(1), r.GetString(2), FromUnix(r.GetInt64(3)), FromUnix(r.GetInt64(4)),
                    r.IsDBNull(5) ? null : r.GetInt32(5)));
            return rows;
        }
    }

    public void AddSpeedTest(SpeedRun s) => Exec(
        "insert or replace into speed_tests values ($ts, $d, $u, $p, $c, $b)",
        ("$ts", Unix(s.Time)), ("$d", s.DownMbps), ("$u", s.UpMbps), ("$p", s.PingMs), ("$c", s.Connection), ("$b", s.BackgroundMbps));

    public List<SpeedRun> SpeedTests(DateTime from)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select ts, down, up, ping, connection, background from speed_tests where ts >= $from order by ts";
            cmd.Parameters.AddWithValue("$from", Unix(from));
            using var r = cmd.ExecuteReader();
            var rows = new List<SpeedRun>();
            while (r.Read())
                rows.Add(new SpeedRun(FromUnix(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetString(4), N(r, 5)));
            return rows;
        }
    }

    /// <summary>Events since a time, oldest first, of one kind or all.</summary>
    public List<EventRow> EventsSince(DateTime from, string? kind = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select ts, kind, title, detail from events where ts >= $from and ($k is null or kind = $k) order by ts";
            cmd.Parameters.AddWithValue("$from", Unix(from));
            cmd.Parameters.AddWithValue("$k", (object?)kind ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var rows = new List<EventRow>();
            while (r.Read()) rows.Add(new EventRow(FromUnix(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
            return rows;
        }
    }

    public List<MinuteRow> Minutes(DateTime from)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select * from minutes where ts >= $from order by ts";
            cmd.Parameters.AddWithValue("$from", Unix(from));
            using var r = cmd.ExecuteReader();
            var rows = new List<MinuteRow>();
            while (r.Read())
                rows.Add(new MinuteRow(FromUnix(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                    N(r, 5), N(r, 6), N(r, 7), N(r, 8), N(r, 9), r.IsDBNull(10) ? null : r.GetString(10)));
            return rows;
        }
    }

    public List<EventRow> Events(int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select ts, kind, title, detail from events order by ts desc limit $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var rows = new List<EventRow>();
            while (r.Read()) rows.Add(new EventRow(FromUnix(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
            return rows;
        }
    }

    public List<GameSession> Games(int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select start, end, game, gpu_avg, gpu_temp_max, hotspot_max, cpu_temp_max, parked_pct, vram_peak " +
                              "from games order by start desc limit $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var rows = new List<GameSession>();
            while (r.Read())
                rows.Add(new GameSession(r.GetString(2), FromUnix(r.GetInt64(0)), FromUnix(r.GetInt64(1)), r.GetDouble(3),
                    N(r, 4), N(r, 5), N(r, 6), N(r, 7), r.GetDouble(8)));
            return rows;
        }
    }

    public void Prune()
    {
        Exec("delete from minutes where ts < $t", ("$t", Unix(DateTime.Now.AddDays(-90))));
        Exec("delete from events where ts < $t", ("$t", Unix(DateTime.Now.AddDays(-365))));
        Exec("delete from job_runs where end < $t", ("$t", Unix(DateTime.Now.AddDays(-365))));
        Exec("delete from speed_tests where ts < $t", ("$t", Unix(DateTime.Now.AddDays(-365))));
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime;

    public void Dispose() => _db.Dispose();
}

/// <summary>Averages a minute of one-second snapshots into one row (temperatures keep their maximum).</summary>
public sealed class MinuteAggregator
{
    private readonly List<Snapshot> _buffer = [];
    private DateTime _minute = DateTime.MinValue;

    public MinuteRow? Add(Snapshot s)
    {
        var minute = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, s.Time.Minute, 0, s.Time.Kind);
        MinuteRow? done = null;
        if (minute != _minute && _buffer.Count > 0) done = Flush();
        _minute = minute;
        _buffer.Add(s);
        return done;
    }

    private MinuteRow Flush()
    {
        var b = _buffer;
        string? top = b[^1].Workloads.OrderByDescending(w => w.CpuPct + w.GpuPct).FirstOrDefault()?.Label;
        var row = new MinuteRow(_minute, b.Average(s => s.CpuLoad), b.Average(s => s.GpuLoad), b.Average(s => s.RamUsedGb),
            b.Average(s => s.VramUsedGb), Max(b, s => s.CpuTemp), Max(b, s => s.GpuTemp), Max(b, s => s.GpuHotspot),
            Avg(b, s => s.CpuPower), Avg(b, s => s.GpuPower), top);
        _buffer.Clear();
        return row;
    }

    private static double? Max(List<Snapshot> b, Func<Snapshot, double?> f) => b.Select(f).Where(v => v is not null).DefaultIfEmpty().Max();
    private static double? Avg(List<Snapshot> b, Func<Snapshot, double?> f) =>
        b.Select(f).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Average() is var a && double.IsNaN(a) ? null : a;
}
