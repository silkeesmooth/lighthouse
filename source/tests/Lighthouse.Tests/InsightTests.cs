using Lighthouse.Core;
using Lighthouse.Core.History;
using Lighthouse.Core.Tracking;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

public class JobTimingTests
{
    [Theory]
    [InlineData("ETL: load --region west --run", "load")]
    [InlineData("ETL: --verbose backfill run x.py", "backfill")]
    [InlineData("ETL", "")]
    public void Runs_are_timed_by_their_command(string label, string kind) => Assert.Equal(kind, JobText.RunKind(label));

    [Fact]
    public void Usual_is_the_median_of_the_last_five()
    {
        var minutes = new[] { 130, 10, 125, 140, 128, 500 }.Select(m => TimeSpan.FromMinutes(m)); // newest first; 500 is the 6th
        Assert.Equal(TimeSpan.FromMinutes(128), JobText.Usual(minutes));
        Assert.Null(JobText.Usual([TimeSpan.FromMinutes(5)])); // one run says nothing yet
    }

    [Theory]
    [InlineData(90, 130, "usually 2h 10m · about 40 min left")]
    [InlineData(10, 130, "usually 2h 10m · about 2h 00m left")]
    [InlineData(130, 130, "usually 2h 10m · should finish soon")]
    [InlineData(150, 130, "usually 2h 10m · should finish soon")]
    [InlineData(200, 130, "running longer than usual (usually 2h 10m)")]
    [InlineData(28, 44, "usually 44 min · about 16 min left")] // a usual time has no seconds
    public void Progress_reads_naturally(int elapsedMinutes, int usualMinutes, string text) =>
        Assert.Equal(text, JobText.Progress(TimeSpan.FromMinutes(elapsedMinutes), TimeSpan.FromMinutes(usualMinutes)));

    [Fact]
    public void One_core_is_singular()
    {
        double one = 100.0 / Environment.ProcessorCount;
        Assert.Equal("1 core", Headline.CoresText(one));
        Assert.Equal("2 cores", Headline.CoresText(2 * one));
    }

    [Fact]
    public void Runs_are_kept_and_read_back_newest_first()
    {
        using var store = new HistoryStore(HistoryStore.InMemory);
        var start = new DateTime(2026, 10, 20, 9, 0, 0);
        store.AddJobRun(new JobRun("ETL", "load", "ETL: load --region west", start, start.AddMinutes(130), 0));
        store.AddJobRun(new JobRun("ETL", "load", "ETL: load --region east", start.AddDays(1), start.AddDays(1).AddMinutes(90), 4));
        store.AddJobRun(new JobRun("ETL", "backfill", "ETL: backfill status", start, start.AddMinutes(1), 0));
        var loads = store.JobRuns("ETL", "load");
        Assert.Equal(["ETL: load --region east", "ETL: load --region west"], loads.Select(r => r.Label));
        Assert.Equal((4, false), (loads[0].Exit, loads[0].Succeeded));
        Assert.Equal(3, store.JobRuns().Count);
    }
}

public class ClaudeProjectTests
{
    [Theory]
    [InlineData(@"C:\Users\you\Documents\lighthouse", "lighthouse")]
    [InlineData(@"C:\Users\you\Documents\lighthouse\", "lighthouse")]
    [InlineData(@"C:\Users\you\Documents\shop-app\.claude\worktrees\eager-fox", "shop-app")]
    [InlineData(@"C:\Users\you", "home")]
    [InlineData(@"C:\", null)]
    [InlineData(null, null)]
    public void Sessions_are_named_after_their_project(string? folder, string? name) =>
        Assert.Equal(name, ClaudeCodeProject.Name(folder, @"C:\Users\you"));

    [Fact]
    public void Two_sessions_in_one_project_share_a_row_with_a_count()
    {
        var classifier = new Classifier(ownPid: 1);
        const string code = @"C:\Users\you\AppData\Local\Claude-Data\claude_x\claude-code\2.1\a\claude.exe";
        ProcessFacts F(int pid, string folder) => new(pid, 5, "claude.exe", code, null, pid, Folder: folder);
        var a = classifier.Classify(F(10, @"C:\src\lighthouse"));
        var b = classifier.Classify(F(11, @"C:\src\lighthouse"));
        var c = classifier.Classify(F(12, @"C:\src\shop"));
        Assert.Equal(("Claude Code · lighthouse", a.Key), (a.Label, b.Key));
        Assert.NotEqual(a.Key, c.Key);
        var usage = new[] { a, b, c }.Select((k, i) => new ProcessUsage(10 + i, "claude.exe", new Resolved(k.Key, k.Label, k.Kind, 10 + i),
            0.1, 1 << 20, 0, 0, new Dictionary<int, double>(), DateTime.Now)).ToList();
        var labels = Aggregator.Aggregate(usage, 10, 8, 1).All.Select(w => w.Label).ToList();
        Assert.Contains("Claude Code · lighthouse (2)", labels);
        Assert.Contains("Claude Code · shop", labels);
    }
}

public class InsightTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0); // a Monday

    [Fact]
    public void A_speed_test_against_the_plan()
    {
        Assert.Equal("6% of your plan down (500 Mbps), 32% up (50 Mbps)", Insights.PlanShare(29, 16, new InternetPlan(500, 50)));
        Assert.Null(Insights.PlanShare(29, 16, null));
    }

    [Fact]
    public void The_week_in_a_few_lines()
    {
        var games = new[]
        {
            new GameSession("Battlefield 6", Now.AddDays(-3), Now.AddDays(-3).AddHours(3), 90, 74, 92, 70, 95, 12),
            new GameSession("Battlefield 6", Now.AddDays(-2), Now.AddDays(-2).AddHours(1), 90, 74, 92, 70, 95, 12),
            new GameSession("Hollow Knight", Now.AddDays(-1), Now.AddDays(-1).AddMinutes(30), 20, 50, 60, 55, 95, 2),
        };
        var minutes = new[] { new MinuteRow(Now.AddDays(-1), 50, 90, 20, 10, 88, 81, 95, null, null, null) };
        var jobs = new[]
        {
            new JobRun("ETL", "load", "ETL: load", Now.AddDays(-4), Now.AddDays(-4).AddHours(2), 0),
            new JobRun("ETL", "load", "ETL: load", Now.AddDays(-2), Now.AddDays(-2).AddHours(2), 4),
        };
        var speeds = new[] { new SpeedRun(Now.AddDays(-5), 25, 16, 80, "Ethernet", null), new SpeedRun(Now.AddDays(-1), 35, 18, 60, "Ethernet", null) };
        var alerts = new[] { new EventRow(Now.AddDays(-2), "alert", "CPU is hot", ""), new EventRow(Now.AddDays(-1), "alert", "CPU is hot", "") };
        Assert.Equal(
        [
            "Gaming: 4h 30m across 3 sessions, most of it Battlefield 6 (4h 00m).",
            "Peaks: CPU 88°C, GPU 81°C (hotspot 95°C).",
            "ETL: 2 runs, 1 stopped (exit 4).",
            "Internet: 25 to 35 Mbps down across 2 tests.",
            "Alerts: 2 (2 cpu is hot).",
        ], Insights.WeeklyDigest(games, minutes, jobs, speeds, alerts));
        Assert.Empty(Insights.WeeklyDigest([], [], [], [], []));
    }

    private static List<MinuteRow> Minutes(DateTime from, int count, double load, double temp) =>
        Enumerable.Range(0, count).Select(i => new MinuteRow(from.AddMinutes(i), load, 0, 0, 0, temp, null, null, null, null, null)).ToList();

    [Fact]
    public void Cooling_fails_when_the_same_load_runs_hotter()
    {
        var history = Minutes(Now.AddDays(-45), 120, 40, 62).Concat(Minutes(Now.AddDays(-2), 120, 40, 69)).ToList();
        var r = Insights.CoolingTrend(history, Now)!;
        Assert.False(r.Ok);
        Assert.StartsWith("At the same load the CPU runs 7 °C hotter than a month ago (62 to 69°C under medium load).", r.Detail);
    }

    [Fact]
    public void Cooling_passes_when_nothing_changed_and_stays_quiet_without_enough_data()
    {
        var steady = Minutes(Now.AddDays(-45), 120, 40, 62).Concat(Minutes(Now.AddDays(-2), 120, 40, 64)).ToList();
        Assert.True(Insights.CoolingTrend(steady, Now)!.Ok);
        var thin = Minutes(Now.AddDays(-45), 30, 40, 62).Concat(Minutes(Now.AddDays(-2), 120, 40, 75)).ToList();
        Assert.Null(Insights.CoolingTrend(thin, Now)); // under an hour a month ago: no verdict
        var otherLoad = Minutes(Now.AddDays(-45), 120, 20, 50).Concat(Minutes(Now.AddDays(-2), 120, 80, 85)).ToList();
        Assert.Null(Insights.CoolingTrend(otherLoad, Now)); // different loads are never compared
    }

    [Fact]
    public void Speed_tests_are_kept()
    {
        using var store = new HistoryStore(HistoryStore.InMemory);
        store.AddSpeedTest(new SpeedRun(Now, 29, 16, 88, "Ethernet", 43));
        var t = store.SpeedTests(Now.AddDays(-1)).Single();
        Assert.Equal((29.0, 16.0, "Ethernet", 43.0), (t.DownMbps, t.UpMbps, t.Connection, t.BackgroundMbps!.Value));
    }
}
