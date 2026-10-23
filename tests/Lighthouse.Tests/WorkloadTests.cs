using Lighthouse.Core.Model;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

/// <summary>Watched jobs as a settings.json would list them: a Python pipeline, its dev server and its tests.</summary>
internal static class TestJobs
{
    public static readonly JobRule Pipeline = new()
    {
        Name = "Pipeline",
        Match = @"-m\s+pipeline\b",
        Process = "python",
        ExitCodes = new() { ["4"] = "stopped: spend cap or outage (exit 4)" },
    };

    public static readonly JobRule ReviewApp = new()
    {
        Name = "Review app", Match = "review_app", Label = @"port\s*=\s*'?(?<label>\d+)", Notify = false,
    };

    public static readonly JobRule Tests = new() { Name = "Pipeline tests", Match = @"-m\s+pytest\b.*pipeline", Notify = false };

    public static readonly JobRule[] All = [ReviewApp, Tests, Pipeline];
}

/// <summary>Command lines and paths as Windows reported them on a real PC (2026-10-21).</summary>
public class ClassifierTests
{
    private const string Llama = @"C:\llm\bin\llama-server.exe -m C:\llm\models\Qwen3-30B-A3B-UD-Q4_K_XL.gguf " +
        @"--host 127.0.0.1 --port 8081 --api-key-file C:\llm\api_key.txt -ngl 99 " +
        @"-c 32768 --log-file C:\llm\logs\server.log";

    private const string ReviewApp = ".venv/Scripts/python.exe -c \"import os, sys, pathlib; d = pathlib.Path('pipeline').resolve(); " +
        "os.chdir(d); sys.path.insert(0, str(d.parent)); sys.path.insert(0, str(d)); import uvicorn; " +
        "uvicorn.run('review_app.app:app', host='127.0.0.1', port=8000)\"";

    private const string StorePython = @"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.11_3.11.2544.0_x64__qbz5n2kfra8p0\python3.11.exe";

    private static readonly Classifier Sut = new(ownPid: 999, jobs: TestJobs.All);

    private static Classification C(string name, string? path, string? cmd = null, int pid = 100, int parent = 50) =>
        Sut.Classify(new ProcessFacts(pid, parent, name, path, cmd, 0));

    [Fact]
    public void Llama_server_is_the_local_llm_named_by_its_model()
    {
        var c = C("llama-server.exe", @"C:\llm\bin\llama-server.exe", Llama);
        Assert.Equal(WorkloadKind.LocalLlm, c.Kind);
        Assert.Equal("Local LLM · Qwen3-30B-A3B", c.Label);
    }

    [Fact]
    public void A_name_for_the_llm_folder_covers_its_tools_but_not_the_server()
    {
        // 2026-10-22: an installer downloading a model into the server's folder lit the moon as if the server were up.
        var named = new Classifier(ownPid: 999, [new WorkloadOverride(@"\llm\", "Local LLM tools")]);
        ProcessFacts P(string name, string path, string cmd) => new(100, 50, name, path, cmd, 0);
        var installer = named.Classify(P("python.exe", @"C:\llm\tools\python\python.exe",
            "\"C:\\llm\\tools\\python\\python.exe\" \"C:\\llm\\tools\\get_model.py\" download qwen3"));
        var server = named.Classify(P("llama-server.exe", @"C:\llm\bin\llama-server.exe", Llama));
        Assert.Equal(("Local LLM tools", WorkloadKind.App), (installer.Label, installer.Kind));
        Assert.Equal(WorkloadKind.LocalLlm, server.Kind);
    }

    [Fact]
    public void Claude_code_sessions_and_the_desktop_app_are_told_apart()
    {
        var code = C("claude.exe", @"C:\Users\you\AppData\Local\Claude-Data\claude_abc123\claude-code\2.1.0\abc\claude.exe");
        var app = C("claude.exe", @"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__abc123\app\claude.exe");
        Assert.Equal(WorkloadKind.ClaudeCode, code.Kind);
        Assert.Equal(WorkloadKind.ClaudeApp, app.Kind);
    }

    [Fact]
    public void A_job_set_not_to_notify_is_a_service_labelled_by_its_pattern()
    {
        var c = C("python.exe", @"C:\src\pipeline\.venv\Scripts\python.exe", ReviewApp);
        Assert.Equal(WorkloadKind.Service, c.Kind);
        Assert.Equal("Review app: 8000", c.Label);
    }

    [Fact]
    public void A_job_run_is_named_with_its_arguments()
    {
        var c = C("python3.11.exe", StorePython, $"\"{StorePython}\" -m pipeline load --region west --run");
        Assert.Equal(WorkloadKind.Job, c.Kind);
        Assert.Equal("Pipeline: load --region west --run", c.Label);
        Assert.Same(TestJobs.Pipeline, c.Job);
    }

    [Fact]
    public void The_first_matching_job_wins()
    {
        var c = C("python.exe", null, ".venv\\Scripts\\python -m pytest pipeline/tests/test_launch.py -q");
        Assert.Equal(WorkloadKind.Service, c.Kind);
        Assert.Equal("Pipeline tests: pipeline/tests/test_launch.py -q", c.Label);
    }

    [Fact]
    public void A_shell_that_launches_a_job_is_not_the_job()
    {
        // The rule names its program, so `bash -c "python -m pipeline ..."` stays a shell and its python is the run.
        var c = C("bash.exe", @"C:\Program Files\Git\bin\bash.exe", "bash -c \"python -m pipeline load --region west\"");
        Assert.Equal(WorkloadKind.Shell, c.Kind);
    }

    [Fact]
    public void Without_job_rules_a_script_is_just_its_program() =>
        Assert.Equal(WorkloadKind.App, new Classifier(ownPid: 999)
            .Classify(new ProcessFacts(100, 50, "python.exe", null, "python -m pipeline launch", 0)).Kind);

    [Fact]
    public void Ai_apps_share_a_kind()
    {
        Assert.Equal(WorkloadKind.AiApp, C("ChatGPT.exe", @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2025.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe").Kind);
        Assert.Equal(WorkloadKind.AiApp, C("copilot.exe", null).Kind);
        Assert.True(WorkloadKinds.IsAi(WorkloadKind.ClaudeApp));
        Assert.False(WorkloadKinds.IsAi(WorkloadKind.App));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 6\bf6.exe", "bf6.exe", "Battlefield 6")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
        "FortniteClient-Win64-Shipping.exe", "Fortnite")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Poly Bridge 3\Poly Bridge 3.exe", "Poly Bridge 3.exe", "Poly Bridge 3")]
    public void Games_are_named_after_their_folder(string path, string exe, string game)
    {
        var c = C(exe, path);
        Assert.Equal(WorkloadKind.Game, c.Kind);
        Assert.Equal(game, c.Label);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 6\EAAntiCheat.GameServiceLauncher.exe", "EAAntiCheat.GameServiceLauncher.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Epic Online Services\managedArtifacts\98bc04bc842e4906993fd6d6644ffb8d\EOSOverlayRenderer-Win64-Shipping.exe",
        "EOSOverlayRenderer-Win64-Shipping.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Steamworks Shared\_CommonRedist\vcredist\2022\VC_redist.x64.exe", "VC_redist.x64.exe")]
    public void Launchers_overlays_and_anti_cheat_are_not_games(string path, string exe) =>
        Assert.NotEqual(WorkloadKind.Game, C(exe, path).Kind);

    [Fact]
    public void Codex_app_is_codex_even_though_its_exe_is_named_chatgpt() =>
        Assert.Equal("Codex", C("ChatGPT.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe").Label);

    [Fact]
    public void Windows_maintenance_gets_its_own_name()
    {
        Assert.Equal("Microsoft Defender", C("MsMpEng.exe", null).Label);
        Assert.Equal("Windows Update", C("TiWorker.exe", @"C:\Windows\WinSxS\amd64_x\TiWorker.exe").Label);
        Assert.Equal(WorkloadKind.Windows, C("svchost.exe", @"C:\WINDOWS\system32\svchost.exe").Kind);
    }

    [Fact]
    public void Lighthouse_knows_itself() => Assert.Equal(WorkloadKind.Lighthouse, C("Lighthouse.exe", null, pid: 999).Kind);

    [Fact]
    public void Unknown_programs_are_named_after_their_exe_and_may_join_a_parent()
    {
        var c = C("ShareX.exe", @"C:\Program Files\ShareX\ShareX.exe");
        Assert.Equal("ShareX", c.Label);
        Assert.True(c.Generic);
    }
}

public class ResolverTests
{
    private static Dictionary<int, Resolved> Resolve(params (int Pid, int Parent, string Name, string? Path, string? Cmd, long Created)[] procs)
    {
        var classifier = new Classifier(ownPid: 1, jobs: TestJobs.All);
        var facts = procs.ToDictionary(p => p.Pid, p => new ProcessFacts(p.Pid, p.Parent, p.Name, p.Path, p.Cmd, p.Created));
        var own = facts.ToDictionary(kv => kv.Key, kv => classifier.Classify(kv.Value));
        return Resolver.Resolve(facts, own);
    }

    private const string Explorer = @"C:\WINDOWS\explorer.exe";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Fact]
    public void A_job_runs_chrome_counts_as_the_run_not_as_chrome()
    {
        var r = Resolve(
            (10, 5, "explorer.exe", Explorer, null, 1),
            (20, 10, "python3.11.exe", null, "python -m pipeline load --region west --run", 2),
            (21, 20, "python.exe", null, ".venv\\Scripts\\python.exe -m pipeline load --region west --run", 3),
            (30, 21, "chrome.exe", Chrome, null, 4),
            (40, 10, "chrome.exe", Chrome, null, 5));
        Assert.Equal("job:pipeline:20", r[21].Key); // a launcher's re-run of itself folds into the outer run
        Assert.Equal("job:pipeline:20", r[30].Key);
        Assert.Equal("Pipeline: load --region west --run", r[30].Label);
        Assert.Same(TestJobs.Pipeline, r[30].Job);
        Assert.Equal("app:chrome", r[40].Key); // the person's own Chrome stays Chrome
    }

    [Fact]
    public void Two_runs_at_once_are_two_rows()
    {
        var r = Resolve(
            (20, 5, "python.exe", null, "python -m pipeline load --region west --run", 2),
            (30, 5, "python.exe", null, "python -m pipeline load --region east --run", 3));
        Assert.NotEqual(r[20].Key, r[30].Key);
    }

    [Fact]
    public void Claude_code_owns_the_tools_it_runs()
    {
        const string code = @"C:\Users\you\AppData\Local\Claude-Data\claude_abc123\claude-code\2.1.0\x\claude.exe";
        var r = Resolve(
            (10, 5, "claude.exe", code, null, 1),
            (11, 10, "bash.exe", @"C:\Program Files\Git\bin\bash.exe", null, 2),
            (12, 11, "grep.exe", @"C:\Program Files\Git\usr\bin\grep.exe", null, 3),
            (13, 10, "python.exe", null, "python -m pipeline backfill status", 4));
        Assert.Equal("claude-code", r[12].Key);
        Assert.Equal(WorkloadKind.Job, r[13].Kind); // a run it starts is still its own row
    }

    [Fact]
    public void Conhost_joins_the_console_app_it_serves()
    {
        var r = Resolve(
            (10, 5, "llama-server.exe", @"C:\llm\bin\llama-server.exe", "llama-server.exe -m x.gguf", 1),
            (11, 10, "conhost.exe", @"C:\WINDOWS\system32\conhost.exe", null, 2));
        Assert.Equal("llm", r[11].Key);
    }

    [Fact]
    public void Something_explorer_started_does_not_become_file_explorer()
    {
        var r = Resolve(
            (10, 5, "explorer.exe", Explorer, null, 1),
            (11, 10, "ShareX.exe", @"C:\Program Files\ShareX\ShareX.exe", null, 2));
        Assert.Equal("app:sharex", r[11].Key);
    }

    [Fact]
    public void A_reused_parent_pid_is_not_a_parent()
    {
        // pid 10 started after pid 20, so it cannot be 20's parent (20's real parent exited and the pid was reused).
        var r = Resolve(
            (10, 5, "python.exe", null, "python -m pipeline load --region north --run", 9),
            (20, 10, "chrome.exe", Chrome, null, 3));
        Assert.Equal("app:chrome", r[20].Key);
    }
}

public class AggregatorTests
{
    private static ProcessUsage P(int pid, string key, WorkloadKind kind, double cpu, double ramGb, double vramGb = 0,
        long sharedGb = 0, params (int Engine, double Value)[] gpu) =>
        new(pid, $"p{pid}.exe", new Resolved(key, key, kind, pid), cpu, (long)(ramGb * (1L << 30)), sharedGb << 30,
            vramGb * (1L << 30), gpu.ToDictionary(g => g.Engine, g => g.Value), DateTime.Now);

    private static List<ProcessUsage> Machine() =>
    [
        P(1, "chrome", WorkloadKind.App, 0.020, 1.5, 0.4, gpu: [(0, 3)]),
        P(2, "chrome", WorkloadKind.App, 0.010, 0.5),
        P(3, "llm", WorkloadKind.LocalLlm, 0.001, 0.8, 17.9, sharedGb: 18),
        P(4, "claude-code", WorkloadKind.ClaudeCode, 0.004, 1.3),
        P(5, "windows", WorkloadKind.Windows, 0.002, 2.1, 0.2),
        P(6, "lighthouse", WorkloadKind.Lighthouse, 0.001, 0.06),
        P(7, "app:a", WorkloadKind.App, 0.0005, 0.2),
        P(8, "app:b", WorkloadKind.App, 0.0005, 0.1),
        P(9, "app:c", WorkloadKind.App, 0.0001, 0.05),
        P(10, "app:d", WorkloadKind.App, 0.0001, 0.05),
    ];

    [Theory]
    [InlineData(2.04, 39.38, 19.54)]
    [InlineData(97.0, 60.0, 19.9)]
    [InlineData(0.0, 10.0, 0.5)]
    public void Rows_plus_everything_else_equal_the_totals(double cpu, double ram, double vram)
    {
        var t = Aggregator.Aggregate(Machine(), cpu, ram, vram);
        Assert.Equal(cpu, t.Top.Sum(w => w.CpuPct) + t.Rest.CpuPct, 6);
        Assert.Equal(ram, t.Top.Sum(w => w.RamGb) + t.Rest.RamGb, 6);
        Assert.Equal(vram, t.Top.Sum(w => w.VramGb) + t.Rest.VramGb, 6);
        Assert.Equal(Machine().Count, t.Top.Sum(w => w.Processes) + t.Rest.Processes);
    }

    [Fact]
    public void Cpu_is_scaled_to_the_task_manager_total()
    {
        var t = Aggregator.Aggregate(Machine(), 40, 50, 19.9);
        var all = t.All.Sum(w => w.CpuPct);
        Assert.Equal(40, all, 6);
    }

    [Fact]
    public void The_llms_mapped_model_counts_as_its_ram()
    {
        var t = Aggregator.Aggregate(Machine(), 5, 39.4, 19.5);
        Assert.Equal(18.8, t.All.Single(w => w.Key == "llm").RamGb, 1);
        Assert.Equal(2.0, t.All.Single(w => w.Key == "chrome").RamGb, 3); // everyone else: private working set only
    }

    [Fact]
    public void Lighthouse_is_a_footnote_not_a_row()
    {
        var t = Aggregator.Aggregate(Machine(), 5, 39.4, 19.5);
        Assert.DoesNotContain(t.Top, w => w.Kind == WorkloadKind.Lighthouse);
        Assert.NotNull(t.Self);
    }

    [Fact]
    public void Rows_that_claim_more_than_the_total_are_scaled_to_fit()
    {
        var t = Aggregator.Aggregate(Machine(), 5, 3.0, 1.0); // counters read a moment apart
        Assert.Equal(3.0, t.Top.Sum(w => w.RamGb) + t.Rest.RamGb, 6);
        Assert.Equal(1.0, t.Top.Sum(w => w.VramGb) + t.Rest.VramGb, 6);
        Assert.True(t.Rest.RamGb >= 0 && t.Rest.VramGb >= 0);
    }

    [Fact]
    public void Gpu_is_the_busiest_engine_and_the_rest_has_none()
    {
        var procs = new List<ProcessUsage>
        {
            P(1, "game", WorkloadKind.Game, 0.1, 4, 8, gpu: [(0, 60), (2, 10)]),
            P(2, "game", WorkloadKind.Game, 0.0, 0.1, 0, gpu: [(0, 30)]),
        };
        var t = Aggregator.Aggregate(procs, 10, 10, 10);
        Assert.Equal(90, t.Top.Single().GpuPct, 6);
        Assert.True(double.IsNaN(t.Rest.GpuPct));
    }

    [Fact]
    public void Claude_code_label_counts_sessions()
    {
        var procs = new List<ProcessUsage>
        {
            new(1, "claude.exe", new Resolved("claude-code", "Claude Code", WorkloadKind.ClaudeCode, 1), 0.01, 1 << 30, 0, 0, new Dictionary<int, double>(), DateTime.Now),
            new(2, "claude.exe", new Resolved("claude-code", "Claude Code", WorkloadKind.ClaudeCode, 2), 0.01, 1 << 30, 0, 0, new Dictionary<int, double>(), DateTime.Now),
            new(3, "bash.exe", new Resolved("claude-code", "Claude Code", WorkloadKind.ClaudeCode, 1), 0.01, 1 << 20, 0, 0, new Dictionary<int, double>(), DateTime.Now),
        };
        Assert.Equal("Claude Code (2)", Aggregator.Aggregate(procs, 3, 10, 1).Top.Single().Label);
    }
}
