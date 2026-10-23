using System.Drawing;

namespace Lighthouse.Core.Rendering;

/// <summary>
/// The numbers behind the lighthouse scene, kept apart from the drawing so they can be tested:
/// sky colours by the hour, how far the sea rises with RAM, how fast the beam turns with CPU load.
/// </summary>
public static class SceneMath
{
    // (hour, sky top, sky at the horizon). Payne's gray by day, ink at night, bittersweet at sunset.
    private static readonly (double Hour, Color Top, Color Bottom)[] Sky =
    [
        (0.0, Hex("#0b1519"), Hex("#1d2c31")),
        (5.0, Hex("#142a33"), Hex("#2e6171")),
        (6.5, Hex("#2e6171"), Hex("#f0c48a")),
        (8.0, Hex("#7fb3c2"), Hex("#d9e5d6")),
        (12.0, Hex("#8dc0cf"), Hex("#edf3f5")),
        (17.0, Hex("#6fa3b3"), Hex("#ead9a8")),
        (19.0, Hex("#2e6171"), Hex("#e26d5c")),
        (20.5, Hex("#172327"), Hex("#2e4a55")),
        (24.0, Hex("#0b1519"), Hex("#1d2c31")),
    ];

    public static (Color Top, Color Bottom) SkyAt(double hour)
    {
        hour = ((hour % 24) + 24) % 24;
        for (int i = 0; i < Sky.Length - 1; i++)
        {
            var (h0, t0, b0) = Sky[i];
            var (h1, t1, b1) = Sky[i + 1];
            if (hour < h0 || hour > h1) continue;
            double f = (hour - h0) / (h1 - h0);
            return (Mix(t0, t1, f), Mix(b0, b1, f));
        }
        return (Sky[0].Top, Sky[0].Bottom);
    }

    /// <summary>1 at night, 0 by day, fading over dusk (19-21) and dawn (5-7). Stars and the beam follow it.</summary>
    public static double Night(double hour)
    {
        hour = ((hour % 24) + 24) % 24;
        if (hour >= 21 || hour < 5) return 1;
        if (hour >= 19) return (hour - 19) / 2;
        if (hour < 7) return 1 - (hour - 5) / 2;
        return 0;
    }

    /// <summary>Water line as a fraction of the scene's height from the top: the sea rises as RAM fills.</summary>
    public static double SeaLevel(double ramFraction) => 0.90 - 0.35 * Math.Clamp(ramFraction, 0, 1);

    /// <summary>Beam speed in radians a second: a slow sweep when idle (about 9 s a turn), about 1 s flat out.</summary>
    public static double BeamSpeed(double cpuLoad) => 0.7 + 5.3 * Math.Clamp(cpuLoad / 100, 0, 1);

    /// <summary>Wave height in pixels: a calm sea when the GPU idles, chop when it works.</summary>
    public static double WaveHeight(double gpuLoad) => 1.2 + 5.5 * Math.Clamp(gpuLoad / 100, 0, 1);

    /// <summary>
    /// How much light a speed test sends across the scene, and how many trips a second: 3 slow lights before there is
    /// a reading, 12 quick ones at a gigabit. On a log scale, so 10, 100 and 1000 Mbps each look different.
    /// </summary>
    public static (int Count, double Rate) SignalFlow(double? mbps)
    {
        double level = Math.Clamp(Math.Log10(1 + Math.Max(0, mbps ?? 0)) / 3, 0, 1); // 0 at 0 Mbps, 1 at ~1 Gbps
        return ((int)Math.Round(3 + 9 * level), 0.45 + 1.15 * level);
    }

    /// <summary>
    /// A boat's name tag: the run's first two words that aren't flags. "Import: load --region west --full" -> "load west",
    /// "Import: backfill run x.py" -> "backfill run"; a run with no arguments is the job's name.
    /// </summary>
    public static string ShortRunLabel(string label)
    {
        int colon = label.IndexOf(": ", StringComparison.Ordinal);
        string text = colon > 0 ? label[(colon + 2)..] : "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith('-')).Take(2).ToList();
        return words.Count > 0 ? string.Join(" ", words) : colon > 0 ? label[..colon] : label;
    }

    public static Color Mix(Color a, Color b, double f)
    {
        f = Math.Clamp(f, 0, 1);
        int L(int x, int y) => (int)Math.Round(x + (y - x) * f);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}
