using Lighthouse.Core;
using Lighthouse.Core.Alerts;
using Lighthouse.Core.Model;

namespace Lighthouse.Tests;

public class AlertEngineTests
{
    private DateTime _now = new(2026, 10, 21, 21, 0, 0);
    private AlertEngine NewEngine(Thresholds? t = null) => new(t ?? new Thresholds(), () => _now);
    private static readonly AlertContext Normal = new(InGame: false, Paused: false, UserIdle: TimeSpan.Zero);

    private static Snapshot Hot(double cpuTemp) => new() { CpuTemp = cpuTemp, CpuLoad = 80 };

    private List<Notice> Run(AlertEngine engine, Func<int, Snapshot> at, int seconds, AlertContext? ctx = null)
    {
        var fired = new List<Notice>();
        for (int i = 0; i < seconds; i++)
        {
            fired.AddRange(engine.Evaluate(at(i), ctx ?? Normal));
            _now = _now.AddSeconds(1);
        }
        return fired;
    }

    [Fact]
    public void A_spike_shorter_than_the_sustain_time_never_fires()
    {
        var engine = NewEngine();
        var fired = Run(engine, i => Hot(i < 15 ? 92 : 60), 60);
        Assert.Empty(fired);
    }

    [Fact]
    public void A_sustained_condition_fires_once_not_every_second()
    {
        var engine = NewEngine();
        var fired = Run(engine, _ => Hot(92), 300);
        Assert.Single(fired);
        Assert.Equal("cpu-temp", fired[0].Id);
        Assert.Contains("92°C", fired[0].Message);
    }

    [Fact]
    public void It_re_arms_after_clearing_and_the_cooldown()
    {
        var engine = NewEngine();
        var first = Run(engine, _ => Hot(92), 30);
        var cool = Run(engine, _ => Hot(60), 60);
        var soon = Run(engine, _ => Hot(92), 30); // inside the 30 min cooldown
        _now = _now.AddMinutes(40);
        Run(engine, _ => Hot(60), 5);
        var later = Run(engine, _ => Hot(92), 30);
        Assert.Single(first);
        Assert.Empty(cool);
        Assert.Empty(soon);
        Assert.Single(later);
    }

    [Fact]
    public void Games_mute_warnings_but_never_criticals()
    {
        var engine = NewEngine();
        var inGame = new AlertContext(InGame: true, Paused: false, UserIdle: TimeSpan.Zero);
        var warn = Run(engine, _ => Hot(92), 60, inGame);
        var critical = Run(engine, _ => Hot(96), 60, inGame);
        Assert.Empty(warn);
        Assert.Single(critical);
        Assert.Equal(Severity.Critical, critical[0].Severity);
    }

    [Fact]
    public void A_muted_warning_does_not_flood_in_when_the_game_ends()
    {
        var engine = NewEngine();
        Run(engine, _ => Hot(92), 60, new AlertContext(InGame: true, Paused: false, UserIdle: TimeSpan.Zero));
        var after = Run(engine, _ => Hot(92), 60);
        Assert.Empty(after);
    }

    [Fact]
    public void Active_conditions_show_even_while_muted()
    {
        var engine = NewEngine();
        Run(engine, _ => Hot(92), 60, new AlertContext(InGame: false, Paused: true, UserIdle: TimeSpan.Zero));
        Assert.Contains(engine.Active, a => a.Id == "cpu-temp");
    }

    [Fact]
    public void Llm_holding_vram_during_a_game_says_so()
    {
        var engine = NewEngine();
        var game = new WorkloadUsage("game:battlefield 6", "Battlefield 6", WorkloadKind.Game, 20, 97, 9, 1.2, 4);
        var llm = new WorkloadUsage("llm", "Local LLM", WorkloadKind.LocalLlm, 0, 0, 18.8, 17.9, 2);
        var s = new Snapshot { VramUsedGb = 19.6, VramTotalGb = 19.9, AllWorkloads = [game, llm] };
        var fired = Run(engine, _ => s, 10);
        Assert.Contains(fired, n => n.Id == "llm-vram" && n.Title.Contains("Battlefield 6"));
    }

    [Fact]
    public void A_process_busy_for_15_minutes_while_you_are_away_is_a_runaway()
    {
        var engine = NewEngine();
        double twoCores = 2 * 100.0 / Environment.ProcessorCount; // CPU % is of the whole machine, whatever its size
        var stuck = new WorkloadUsage("app:node", "node", WorkloadKind.App, twoCores, 0, 0.3, 0, 1);
        var away = new AlertContext(InGame: false, Paused: false, UserIdle: TimeSpan.FromMinutes(30));
        var fired = Run(engine, _ => new Snapshot { AllWorkloads = [stuck] }, 16 * 60, away);
        var runaway = Assert.Single(fired, n => n.Id.StartsWith("runaway:"));
        Assert.Contains("node has used", runaway.Message);
    }

    [Fact]
    public void Job_runs_and_claude_are_never_runaways()
    {
        var engine = NewEngine();
        var run = new WorkloadUsage("job:pipeline:1", "Pipeline: launch", WorkloadKind.Job, 30, 0, 2, 0, 3);
        var claude = new WorkloadUsage("claude-code", "Claude Code (8)", WorkloadKind.ClaudeCode, 10, 0, 2, 0, 20);
        var away = new AlertContext(InGame: false, Paused: false, UserIdle: TimeSpan.FromHours(1));
        Assert.Empty(Run(engine, _ => new Snapshot { AllWorkloads = [run, claude] }, 20 * 60, away));
    }

    [Fact]
    public void Steady_growth_over_half_an_hour_is_a_leak()
    {
        var engine = NewEngine();
        int second = 0;
        var fired = Run(engine, _ =>
        {
            double gb = 1 + second++ / 60.0 * 0.1; // +0.1 GB a minute
            return new Snapshot { AllWorkloads = [new WorkloadUsage("app:x", "x", WorkloadKind.App, 1, 0, gb, 0, 1)] };
        }, 32 * 60);
        Assert.Contains(fired, n => n.Id == "leak:app:x");
    }
}

public class HeadlineTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 22, 0, 0);

    [Fact]
    public void Calm_machine_with_the_llm_loaded()
    {
        var llm = new WorkloadUsage("llm", "Local LLM", WorkloadKind.LocalLlm, 0, 0, 18.8, 18.3, 2);
        var s = new Snapshot { CpuLoad = 2, GpuLoad = 0, VramTotalGb = 19.9, AllWorkloads = [llm], Workloads = [llm] };
        Assert.Equal("Calm. The local LLM holds 18.3 of 20 GB VRAM.", Headline.Build(s, [], Now));
    }

    [Fact]
    public void A_game_reports_gpu_temp_and_the_ccd()
    {
        var game = new WorkloadUsage("game:battlefield 6", "Battlefield 6", WorkloadKind.Game, 20, 97, 9, 8, 4);
        var s = new Snapshot { GpuLoad = 97, GpuTemp = 74, Ccd = CcdState.OtherParked, AllWorkloads = [game] };
        Assert.Equal("Battlefield 6: GPU 97%, 74°C, V-Cache CCD active.", Headline.Build(s, [], Now));
    }

    [Fact]
    public void A_job_run_says_cores_and_time()
    {
        var run = new WorkloadUsage("job:pipeline:1", "Pipeline: load --region west --run", WorkloadKind.Job,
            100.0 * 6 / Environment.ProcessorCount, 0, 2, 0, 5, Started: Now.AddMinutes(-72));
        var s = new Snapshot { CpuLoad = 30, AllWorkloads = [run], Workloads = [run] };
        Assert.Equal("Pipeline load --region west --run is using 6 cores, 1h 12m in.", Headline.Build(s, [], Now));
    }

    [Fact]
    public void An_alert_wins()
    {
        var s = new Snapshot { CpuLoad = 90 };
        var alert = new Notice("cpu-temp", Severity.Warning, "CPU is running hot", "CPU has been at 92°C for 20 s.");
        Assert.Equal("CPU has been at 92°C for 20 s.", Headline.Build(s, [alert], Now));
    }

    [Fact]
    public void Busy_names_the_biggest_load()
    {
        var chrome = new WorkloadUsage("app:chrome", "Chrome", WorkloadKind.App, 55, 2, 3, 0.4, 20);
        var s = new Snapshot { CpuLoad = 70, Workloads = [chrome], AllWorkloads = [chrome] };
        Assert.Equal("Busy: Chrome is using 55% CPU.", Headline.Build(s, [], Now));
    }
}
