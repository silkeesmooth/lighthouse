namespace Lighthouse.Core.History;

/// <summary>Chart points from history: bucketed to the width, broken where nothing was recorded, and time ticks.</summary>
public static class ChartData
{
    /// <summary>
    /// Averages each series into at most <paramref name="buckets"/> equal time buckets between <paramref name="from"/>
    /// and <paramref name="to"/>, or keeps each bucket's highest value with <paramref name="peaks"/>, so a chart's top
    /// matches the "max" beside it. Wherever two buckets with data are further apart than <paramref name="gap"/> (or 2.5
    /// buckets, if that is longer), a null goes between them, so the line breaks over the hours the PC was off or
    /// asleep instead of crossing them.
    /// </summary>
    public static (List<DateTime> Times, List<List<double?>> Series) Bucket(IReadOnlyList<DateTime> times,
        IReadOnlyList<IReadOnlyList<double?>> series, DateTime from, DateTime to, int buckets, TimeSpan gap, bool peaks = false)
    {
        var outTimes = new List<DateTime>();
        var outSeries = series.Select(_ => new List<double?>()).ToList();
        if (to <= from || buckets < 1) return (outTimes, outSeries);
        long width = Math.Max(1, (to - from).Ticks / buckets);
        var maxGap = TimeSpan.FromTicks(Math.Max(gap.Ticks, width * 5 / 2));
        var sums = new double[buckets, series.Count];
        var counts = new int[buckets, series.Count];
        var timeSums = new double[buckets];
        var timeCounts = new int[buckets];
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i] < from || times[i] > to) continue;
            int b = (int)Math.Min(buckets - 1, (times[i] - from).Ticks / width);
            timeSums[b] += (times[i] - from).Ticks;
            timeCounts[b]++;
            for (int s = 0; s < series.Count; s++)
            {
                if (i >= series[s].Count || series[s][i] is not { } v) continue;
                sums[b, s] = peaks ? (counts[b, s] == 0 ? v : Math.Max(sums[b, s], v)) : sums[b, s] + v;
                counts[b, s]++;
            }
        }
        DateTime? last = null;
        for (int b = 0; b < buckets; b++)
        {
            if (timeCounts[b] == 0) continue;
            var at = from + TimeSpan.FromTicks((long)(timeSums[b] / timeCounts[b]));
            if (last is { } previous && at - previous > maxGap)
            {
                outTimes.Add(previous + (at - previous) / 2);
                foreach (var list in outSeries) list.Add(null);
            }
            outTimes.Add(at);
            for (int s = 0; s < series.Count; s++)
                outSeries[s].Add(counts[b, s] == 0 ? null : peaks ? sums[b, s] : sums[b, s] / counts[b, s]);
            last = at;
        }
        return (outTimes, outSeries);
    }

    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
    ];

    /// <summary>
    /// Round times between <paramref name="from"/> and <paramref name="to"/>, no more than <paramref name="most"/> of
    /// them: every 15 minutes over an hour, every 3 or 6 hours over a day, every day over a week.
    /// </summary>
    public static List<DateTime> Ticks(DateTime from, DateTime to, int most)
    {
        var span = to - from;
        var step = Steps.FirstOrDefault(s => span.Ticks / s.Ticks <= most, Steps[^1]);
        // Whole days start at midnight; shorter steps count from midnight of the first day.
        var first = from.Date;
        while (first < from) first += step;
        var ticks = new List<DateTime>();
        for (var t = first; t <= to; t += step) ticks.Add(t);
        return ticks;
    }

    /// <summary>"15:00" for a tick under a day apart, "Tue" for a day or more.</summary>
    public static string TickLabel(DateTime tick, TimeSpan step) =>
        step >= TimeSpan.FromDays(1) ? tick.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture) : tick.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
