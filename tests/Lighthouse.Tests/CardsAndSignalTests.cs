using Lighthouse.Core;
using Lighthouse.Core.Network;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

public class ClosedCardsTests
{
    [Fact]
    public void A_closed_card_stays_hidden_while_it_would_still_show()
    {
        var closed = new ClosedCards();
        closed.Close("alert:cpu-temp");
        Assert.Equal(["health-ok"], closed.Visible(["alert:cpu-temp", "health-ok"], k => k));
        Assert.Equal(["health-ok"], closed.Visible(["alert:cpu-temp", "health-ok"], k => k)); // a second later, still hot
    }

    [Fact]
    public void Once_it_goes_away_the_next_one_like_it_shows_again()
    {
        var closed = new ClosedCards();
        closed.Close("alert:cpu-temp");
        closed.Visible(["alert:cpu-temp"], k => k);
        closed.Visible(["health-ok"], k => k); // the alert cleared
        Assert.Equal(["alert:cpu-temp"], closed.Visible(["alert:cpu-temp"], k => k)); // and fired again: it shows
    }
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
