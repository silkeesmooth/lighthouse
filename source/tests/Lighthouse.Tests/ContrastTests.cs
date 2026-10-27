using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

public class ContrastTests
{
    [Fact]
    public void Luminance_runs_from_black_to_white()
    {
        Assert.Equal(0, Contrast.Luminance(0, 0, 0), 6);
        Assert.Equal(1, Contrast.Luminance(255, 255, 255), 6);
        Assert.Equal(0.216, Contrast.Luminance(128, 128, 128), 3); // mid grey is darker than half, perceptually
    }

    [Theory]
    [InlineData(0.02, false, true)]  // a dark window behind black text: switch to white
    [InlineData(0.02, true, true)]   // ...and stay white
    [InlineData(0.60, true, false)]  // a light window behind white text: switch to black
    [InlineData(0.60, false, false)] // ...and stay black
    public void Dark_backdrops_get_white_text_and_light_ones_black(double behind, bool wasWhite, bool white) =>
        Assert.Equal(white, Contrast.WhiteText(behind, wasWhite));

    [Fact]
    public void A_mid_grey_backdrop_does_not_flip_the_text_back_and_forth()
    {
        // Hovering around the crossover keeps whichever colour it already had.
        Assert.True(Contrast.WhiteText(0.19, wasWhite: true));
        Assert.False(Contrast.WhiteText(0.17, wasWhite: false));
        // Clearly past it, it switches.
        Assert.False(Contrast.WhiteText(0.25, wasWhite: true));
        Assert.True(Contrast.WhiteText(0.10, wasWhite: false));
    }

    [Fact]
    public void The_panel_blends_with_the_desktop_by_its_see_through_level()
    {
        Assert.Equal(0.02, Contrast.Behind(desktop: 0.02, panel: 0.95, panelAlpha: 0), 6);  // clear panel: desktop only
        Assert.Equal(0.95, Contrast.Behind(desktop: 0.02, panel: 0.95, panelAlpha: 1), 6);  // solid (hover): panel only
        Assert.Equal(0.3455, Contrast.Behind(desktop: 0.02, panel: 0.95, panelAlpha: 0.35), 4);
    }

    [Fact]
    public void Each_column_is_averaged_separately()
    {
        // 8x4 image: left half black, right half white.
        const int w = 8, h = 4, stride = w * 4;
        var pixels = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = w / 2; x < w; x++)
                pixels.AsSpan(y * stride + x * 4, 4).Fill(255);
        var columns = Contrast.ColumnLuminance(pixels, w, h, stride, 2);
        Assert.Equal(0, columns[0], 6);
        Assert.Equal(1, columns[1], 6);
    }
}
