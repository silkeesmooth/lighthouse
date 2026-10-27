namespace Lighthouse.Core.Rendering;

/// <summary>How much an area of the screen looks like text.</summary>
public static class TextScore
{
    /// <summary>A brightness step (0 to 1) this sharp between neighbouring pixels is a glyph's edge.</summary>
    private const double Edge = 0.25;

    /// <summary>
    /// The share of sampled pixels with a sharp brightness step to the next pixel right or below, from 32-bit BGRA
    /// rows. Text is dense with those steps; a plain or gradient wallpaper has almost none. Every second pixel is
    /// sampled each way, which is plenty and a quarter of the work.
    /// </summary>
    public static double Of(byte[] bgra, int width, int height, int stride)
    {
        if (width < 3 || height < 3) return 0;
        int edges = 0, samples = 0;
        for (int y = 0; y < height - 1; y += 2)
        {
            for (int x = 0; x < width - 1; x += 2)
            {
                double here = Luma(bgra, y * stride + x * 4);
                double right = Luma(bgra, y * stride + (x + 1) * 4);
                double below = Luma(bgra, (y + 1) * stride + x * 4);
                if (Math.Abs(here - right) > Edge || Math.Abs(here - below) > Edge) edges++;
                samples++;
            }
        }
        return samples == 0 ? 0 : (double)edges / samples;
    }

    private static double Luma(byte[] p, int i) => (0.0722 * p[i] + 0.7152 * p[i + 1] + 0.2126 * p[i + 2]) / 255;
}

/// <summary>A spot the mini could sit at (device pixels), and how much text is under it there.</summary>
public sealed record Spot(string Name, int X, int Y, double Score);

/// <summary>
/// Where the mini goes when it is over text. Pure rules, so they are tested: it stays put unless the area under it
/// reads as text, goes to the calmest spot only when that spot is clearly calmer, and moves at most once every 10 s
/// (a drag counts as a move). It goes back to its favorite spot ("home") as soon as home has been clear for two
/// checks in a row.
/// </summary>
public sealed class MiniPlacement
{
    /// <summary>Above this much text the mini is in the way.</summary>
    public const double Busy = 0.06;
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    /// <summary>How far in from the work area's edges the spots sit, and where a drag snaps to.</summary>
    public const int Margin = 16;

    /// <summary>The spots' names, in menu order.</summary>
    public static readonly string[] Names = ["top-left", "top", "top-right", "right", "bottom-right", "bottom", "bottom-left", "left"];

    private int _busyChecks, _homeClearChecks;
    private DateTime _lastMove = DateTime.MinValue;

    /// <summary>
    /// One check a second. <paramref name="here"/> is the text under the mini now; <paramref name="spots"/> are the
    /// candidates (scored), asked for only when a move is due. Returns where to go, or null to stay.
    /// </summary>
    public Spot? Check(DateTime now, double here, bool atHome, Spot? home, Func<IReadOnlyList<Spot>> spots)
    {
        _busyChecks = here > Busy ? _busyChecks + 1 : 0;
        _homeClearChecks = !atHome && home is { Score: <= Busy } ? _homeClearChecks + 1 : 0;
        if (now - _lastMove < Wait) return null;
        if (_homeClearChecks >= 2) return Moved(now, home!); // home is clear: back to it
        if (_busyChecks < 2) return null; // text under it twice in a row, not a passing flicker
        var best = spots().MinBy(s => s.Score);
        // Only to a spot that is clearly calmer, and calm.
        return best is not null && best.Score <= Busy && best.Score < here * 0.6 ? Moved(now, best) : null;
    }

    /// <summary>It was dragged: that is a move, so it stays there at least <see cref="Wait"/>.</summary>
    public void Dragged(DateTime now)
    {
        _lastMove = now;
        _busyChecks = _homeClearChecks = 0;
    }

    private Spot Moved(DateTime now, Spot to)
    {
        Dragged(now);
        return to;
    }

    /// <summary>One named spot of a work area (see <see cref="Candidates"/>), or null for a name that isn't one.</summary>
    public static (int X, int Y)? Named(string name, int left, int top, int right, int bottom, int width, int height) =>
        Candidates(left, top, right, bottom, width, height).Where(c => c.Name == name).Select(c => ((int, int)?)(c.X, c.Y)).FirstOrDefault();

    /// <summary>
    /// The 4 corners and 4 edge midpoints of a work area, for a window of this size, 16 px in from the edges.
    /// A spot that would cover more than half of an <paramref name="avoid"/> position (where the mini is, and its
    /// favorite spot) is left out: moving there wouldn't get it off what is under it.
    /// </summary>
    public static List<(string Name, int X, int Y)> Candidates(int left, int top, int right, int bottom, int width, int height,
        params (int X, int Y)[] avoid)
    {
        int l = left + Margin, r = right - width - Margin, t = top + Margin, b = bottom - height - Margin;
        int cx = left + (right - left - width) / 2, cy = top + (bottom - top - height) / 2;
        List<(string Name, int X, int Y)> spots =
        [
            ("top-left", l, t), ("top", cx, t), ("top-right", r, t), ("right", r, cy),
            ("bottom-right", r, b), ("bottom", cx, b), ("bottom-left", l, b), ("left", l, cy),
        ];
        long half = (long)width * height / 2;
        return spots.Where(s => !avoid.Any(a =>
            (long)Math.Max(0, width - Math.Abs(s.X - a.X)) * Math.Max(0, height - Math.Abs(s.Y - a.Y)) > half)).ToList();
    }
}
