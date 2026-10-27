using Lighthouse.Core;
using Lighthouse.Core.Network;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

public class SettingsImportTests
{
    [Fact]
    public void Import_replaces_what_the_file_names_and_keeps_the_rest()
    {
        var current = new Settings
        {
            MiniMonitor = true,
            Jobs = [new JobRule { Name = "Old", Match = "old" }],
            WorkloadNames = [new WorkloadOverride("x", "X")],
        };
        const string file = """
            {
              // comments and trailing commas are fine
              "Jobs": [ { "name": "New", "match": "new\\.py" } ],
              "llmStartDirectory": "C:\\llm",
              "autostartOff": [ { "kind": "task", "path": "\\", "name": "VendorUpdate" } ],
            }
            """;
        var (merged, imported, unknown) = current.Merge(file);
        Assert.Equal(["Jobs", "llmStartDirectory", "autostartOff"], imported);
        Assert.Empty(unknown);
        Assert.Equal("New", merged.Jobs.Single().Name); // the whole list is replaced, not appended to
        Assert.Equal(@"C:\llm", merged.LlmStartDirectory);
        Assert.Equal(@"task \VendorUpdate", merged.AutostartOff.Single().ToString());
        Assert.True(merged.MiniMonitor); // not named, so kept
        Assert.Equal("X", merged.WorkloadNames.Single().Label);
    }

    [Fact]
    public void Unknown_settings_are_reported_not_silently_dropped()
    {
        var (_, imported, unknown) = new Settings().Merge("""{ "jobz": [], "miniMonitor": true }""");
        Assert.Equal(["miniMonitor"], imported);
        Assert.Equal(["jobz"], unknown);
    }

    [Fact]
    public void Settings_json_keeps_regular_expressions_readable() =>
        Assert.Contains("\"match\": \"-m\\\\s+etl\"", Settings.Json(new JobRule { Name = "Etl", Match = @"-m\s+etl" }));
}

public class SpeedTestTests
{
    private static readonly ConnectionInfo WiFi = new("Wi-Fi", "Intel(R) Wi-Fi 7 BE200", 2882);

    [Theory]
    [InlineData(1234.5, "1.2 Gbps")]
    [InlineData(412.4, "412 Mbps")]
    [InlineData(8.44, "8.4 Mbps")]
    public void Speeds_read_naturally(double mbps, string text) => Assert.Equal(text, SpeedTest.Format(mbps));

    [Fact]
    public void Megabits_from_bytes() => Assert.Equal(80, SpeedTest.Mbps(10_000_000, TimeSpan.FromSeconds(1)));

    [Fact]
    public void A_result_says_how_fast_and_over_what()
    {
        var r = new SpeedResult(12.4, 412, 38.2, 520_000_000, WiFi);
        Assert.Equal("412 Mbps down, 38 Mbps up, 12 ms ping.", r.Summary);
        Assert.Equal("Over Wi-Fi (Intel(R) Wi-Fi 7 BE200, link 2882 Mbps). Used 520 MB of data.", r.Detail);
    }

    [Fact]
    public void Other_traffic_during_the_test_is_called_out()
    {
        // 2026-10-22, measured: a model download at 34 Mbps left the test 9 Mbps.
        var busy = new SpeedResult(110, 9.1, 18, 21_000_000, null) { BackgroundDownMbps = 33.6, BackgroundUpMbps = 0.7 };
        Assert.Equal(" Something else on this PC was already downloading 34 Mbps when the test started, so the connection can do more than this.",
            busy.BusyNote);
        var quiet = new SpeedResult(12, 412, 38, 1, WiFi) { BackgroundDownMbps = 1.5, BackgroundUpMbps = 0.2 };
        Assert.Equal("", quiet.BusyNote);
        // A little background next to a fast result is noise, not a reason to doubt it.
        Assert.Equal("", (quiet with { BackgroundDownMbps = 30 }).BusyNote);
    }
}
