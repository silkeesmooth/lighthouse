using Lighthouse.Core.History;

namespace Lighthouse.Tests;

public class ChartDataTests
{
    private static readonly DateTime Day = new(2026, 10, 22);

    [Fact]
    public void Hours_with_nothing_recorded_break_the_line()
    {
        // Minute rows 08:00-10:00, the PC off until 14:00, then 14:00-15:00.
        var times = Enumerable.Range(0, 120).Select(m => Day.AddHours(8).AddMinutes(m))
            .Concat(Enumerable.Range(0, 60).Select(m => Day.AddHours(14).AddMinutes(m))).ToList();
        var cpu = times.Select(_ => (double?)10).ToList();
        var (outTimes, series) = ChartData.Bucket(times, [cpu], Day.AddHours(8), Day.AddHours(15), 70, TimeSpan.FromMinutes(3));
        int breaks = series[0].Count(v => v is null);
        Assert.Equal(1, breaks);
        int at = series[0].IndexOf(null);
        Assert.True(outTimes[at] > Day.AddHours(10) && outTimes[at] < Day.AddHours(14));
        Assert.All(series[0].Where(v => v is not null), v => Assert.Equal(10, v));
    }

    [Fact]
    public void A_week_of_minutes_is_averaged_into_buckets_without_false_breaks()
    {
        var times = Enumerable.Range(0, 7 * 24 * 60).Select(m => Day.AddMinutes(m)).ToList();
        var values = times.Select((_, i) => (double?)(i % 2 == 0 ? 0 : 100)).ToList();
        var (outTimes, series) = ChartData.Bucket(times, [values], Day, Day.AddDays(7), 200, TimeSpan.FromMinutes(3));
        Assert.Equal(200, outTimes.Count);
        Assert.DoesNotContain(null, series[0]);
        Assert.All(series[0], v => Assert.InRange(v!.Value, 49, 51));
    }

    [Fact]
    public void Peaks_keep_each_bucket_s_highest_so_the_chart_reaches_the_max()
    {
        var times = Enumerable.Range(0, 60).Select(m => Day.AddMinutes(m)).ToList();
        var values = times.Select((_, i) => (double?)(i == 17 ? 72 : 2)).ToList();
        var (_, series) = ChartData.Bucket(times, [values], Day, Day.AddHours(1), 10, TimeSpan.FromMinutes(3), peaks: true);
        Assert.Equal(72, series[0].Max());
        Assert.Equal(2, series[0].Min());
    }

    [Fact]
    public void A_series_missing_some_readings_keeps_its_own_blanks()
    {
        var times = new[] { Day, Day.AddMinutes(1), Day.AddMinutes(2) };
        double?[] temp = [null, null, 50];
        double?[] cpu = [5, 5, 5];
        var (_, series) = ChartData.Bucket(times, [cpu, temp], Day, Day.AddMinutes(3), 3, TimeSpan.FromMinutes(3));
        Assert.Equal(new double?[] { 5, 5, 5 }, series[0]);
        Assert.Equal(new double?[] { null, null, 50 }, series[1]);
    }

    [Theory]
    [InlineData(1, 15)]     // an hour: every 15 minutes
    [InlineData(24, 360)]   // a day: every 6 hours
    [InlineData(168, 1440)] // a week: every day
    public void Ticks_are_round_and_few(int hours, int stepMinutes)
    {
        var to = Day.AddHours(30).AddMinutes(7);
        var ticks = ChartData.Ticks(to.AddHours(-hours), to, 7);
        Assert.InRange(ticks.Count, 3, 8);
        Assert.All(ticks.Zip(ticks.Skip(1)), p => Assert.Equal(TimeSpan.FromMinutes(stepMinutes), p.Second - p.First));
        Assert.All(ticks, t => Assert.Equal(0, (t - t.Date).Ticks % TimeSpan.FromMinutes(stepMinutes).Ticks));
    }
}
