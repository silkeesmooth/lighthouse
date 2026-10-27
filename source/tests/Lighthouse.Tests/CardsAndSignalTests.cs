using Lighthouse.Core;
using Lighthouse.Core.Network;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

public class ClosedCardsTests
{
    private static readonly DateTime Boot = new(2026, 10, 23, 8, 51, 0);

    [Fact]
    public void A_closed_card_stays_closed_even_when_it_goes_away_and_comes_back()
    {
        var closed = new ClosedCards();
        closed.Close("health:ram-speed");
        Assert.Equal(["health-ok"], closed.Visible(["health:ram-speed", "health-ok"], k => k));
        closed.Visible(["health-ok"], k => k); // it passed for a while
        Assert.Equal(["health-ok"], closed.Visible(["health:ram-speed", "health-ok"], k => k)); // and failed again: still closed
    }

    [Fact]
    public void Closed_cards_survive_a_Lighthouse_restart_but_not_a_Windows_restart()
    {
        string[] saved = ["health:ram-speed", "alert:cpu-temp"];
        // Lighthouse restarted (an update): Windows started at the same time, give or take the clock's rounding.
        Assert.Equal(saved, ClosedCards.Restore(saved, Boot, Boot.AddSeconds(40)));
        // Windows restarted since: everything shows once more.
        Assert.Empty(ClosedCards.Restore(saved, Boot, Boot.AddHours(9)));
        Assert.Empty(ClosedCards.Restore(saved, null, Boot));
    }

    [Fact]
    public void Old_keys_are_dropped_past_the_limit()
    {
        var closed = new ClosedCards(Enumerable.Range(0, ClosedCards.Most).Select(i => $"job:{i}"));
        closed.Close("job:new");
        Assert.Equal(ClosedCards.Most, closed.Keys.Count);
        Assert.Equal("job:1", closed.Keys[0]);
        Assert.Equal("job:new", closed.Keys[^1]);
    }

    [Fact]
    public void Windows_start_time_is_now_minus_its_uptime() =>
        Assert.Equal(Boot, ClosedCards.BootTime(Boot.AddHours(3), 3 * 3_600_000));
}

public class SpeedTestViewTests
{
    [Theory]
    [InlineData(1234.5, "1.2", "Gbps")]
    [InlineData(412.4, "412", "Mbps")]
    [InlineData(8.44, "8.4", "Mbps")]
    public void Big_numbers_keep_their_unit_apart(double mbps, string value, string unit) =>
        Assert.Equal((value, unit), SpeedTest.Parts(mbps));

    [Fact]
    public void The_line_under_the_numbers_names_the_connection()
    {
        var r = new SpeedResult(12, 25, 16, 38_000_000, new("Ethernet", "Realtek PCIe 2.5GbE Family Controller", 1000));
        Assert.Equal("Ethernet · Realtek PCIe 2.5GbE Family Controller · 1000 Mbps link · 38 MB used", r.ConnectionLine);
        Assert.Equal("38 MB used", (r with { Connection = null }).ConnectionLine);
    }

    [Fact]
    public void Faster_connections_send_more_light_faster()
    {
        var none = SceneMath.SignalFlow(null);
        var ten = SceneMath.SignalFlow(10);
        var hundred = SceneMath.SignalFlow(100);
        var gigabit = SceneMath.SignalFlow(1000);
        Assert.Equal((3, 0.45), none);
        Assert.True(ten.Count < hundred.Count && hundred.Count < gigabit.Count);
        Assert.True(ten.Rate < hundred.Rate && hundred.Rate < gigabit.Rate);
        Assert.Equal(12, gigabit.Count);
        Assert.Equal(SceneMath.SignalFlow(5000), gigabit); // capped
    }
}

public class ScheduledTaskPathTests
{
    [Theory]
    [InlineData(@"\Lighthouse\", @"\Lighthouse")] // the form Task Scheduler refuses
    [InlineData(@"\Lighthouse", @"\Lighthouse")]
    [InlineData(@"\", @"\")]
    [InlineData("", @"\")]
    [InlineData(@"Lighthouse\", @"\Lighthouse")]
    public void Folders_are_passed_the_way_Task_Scheduler_takes_them(string folder, string expected) =>
        Assert.Equal(expected, Lighthouse.Core.Autostart.ScheduledTasks.FolderPath(folder));
}
