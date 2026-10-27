namespace Lighthouse.Core.Rendering;

/// <summary>
/// White or black text, chosen from what is really behind it: the desktop, seen through the mini monitor's
/// panel. Uses WCAG relative luminance; white and black give equal contrast at about 0.18.
/// </summary>
public static class Contrast
{
    private const double Crossover = 0.179;
    private const double Margin = 0.03; // keeps a mid-grey window behind the mini from flipping the text back and forth

    public static double Luminance(byte r, byte g, byte b)
    {
        static double Linear(byte c)
        {
            double s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    /// <summary>What the text sits on: the panel colour at its see-through level, over the desktop.</summary>
    public static double Behind(double desktop, double panel, double panelAlpha)
    {
        panelAlpha = Math.Clamp(panelAlpha, 0, 1);
        return panelAlpha * panel + (1 - panelAlpha) * desktop;
    }

    /// <summary>True for white text. Once a side is chosen it holds until the backdrop is clearly the other way.</summary>
    public static bool WhiteText(double behind, bool wasWhite) =>
        wasWhite ? behind < Crossover + Margin : behind < Crossover - Margin;

    /// <summary>Average luminance of each of <paramref name="columns"/> equal-width columns of a 32-bit BGRA image.</summary>
    public static double[] ColumnLuminance(ReadOnlySpan<byte> bgra, int width, int height, int stride, int columns)
    {
        var sums = new double[columns];
        var counts = new int[columns];
        const int step = 3; // every third pixel each way is plenty for an average
        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                int i = y * stride + x * 4;
                int column = Math.Min(columns - 1, x * columns / width);
                sums[column] += Luminance(bgra[i + 2], bgra[i + 1], bgra[i]);
                counts[column]++;
            }
        }
        return sums.Select((s, c) => counts[c] == 0 ? 0.5 : s / counts[c]).ToArray();
    }
}
