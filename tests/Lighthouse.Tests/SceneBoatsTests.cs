using Lighthouse.Core.Model;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

public class SceneBoatsTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0);
    private const int Cpus = 16; // one core = 6.25% of the machine

    private static WorkloadUsage W(string key, WorkloadKind kind, double cpu = 0, double gpu = 0, double down = 0) =>
        new(key, key, kind, cpu, gpu, 1, 0, 1, NetDownMbps: down);

    [Fact]
    public void Jobs_always_sail_and_busy_programs_join_them()
    {
        var boats = new SceneBoats().Pick(
        [
            W("job:backup", WorkloadKind.Job),                      // idle, but a watched job
            W("app:chrome", WorkloadKind.Browser, cpu: 1, down: 40),  // downloading
            W("claude-code:shop", WorkloadKind.ClaudeCode, cpu: 9),  // more than a core
            W("app:vs code", WorkloadKind.App, cpu: 2),               // working, but not hard
            W("app:blender", WorkloadKind.App, gpu: 40),              // rendering on the GPU
        ], T0, Cpus);
        Assert.Equal(["job:backup", "app:chrome", "app:blender", "claude-code:shop"], boats.Select(b => b.Key));
    }

    [Fact]
    public void The_ship_the_moon_and_Windows_never_sail()
    {
        var boats = new SceneBoats().Pick(
        [
            W("game:cyberpunk 2077", WorkloadKind.Game, cpu: 30, gpu: 97),
            W("llm", WorkloadKind.LocalLlm, gpu: 90),
            W("win:windows update", WorkloadKind.Windows, cpu: 20),
            W("lighthouse", WorkloadKind.Lighthouse, cpu: 10),
        ], T0, Cpus);
        Assert.Empty(boats);
    }

    [Fact]
    public void A_boat_stays_half_a_minute_after_its_program_calms_down()
    {
        var picker = new SceneBoats();
        Assert.Single(picker.Pick([W("app:chrome", WorkloadKind.Browser, down: 50)], T0, Cpus));
        Assert.Single(picker.Pick([W("app:chrome", WorkloadKind.Browser, down: 1)], T0.AddSeconds(29), Cpus));
        Assert.Empty(picker.Pick([W("app:chrome", WorkloadKind.Browser, down: 1)], T0.AddSeconds(31), Cpus));
    }

    [Fact]
    public void No_more_than_six_and_the_busiest_win()
    {
        var all = Enumerable.Range(1, 9).Select(i => W($"app:{i}", WorkloadKind.App, cpu: i * 7)).ToList();
        var boats = new SceneBoats().Pick(all, T0, Cpus);
        Assert.Equal(SceneBoats.Most, boats.Count);
        Assert.Equal(["app:9", "app:8", "app:7", "app:6", "app:5", "app:4"], boats.Select(b => b.Key));
    }
}
