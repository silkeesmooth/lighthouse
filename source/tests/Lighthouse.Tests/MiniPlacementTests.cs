using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

[Collection("GDI handles")] // draws bitmaps: not alongside the handle-leak count
public class TextScoreTests
{
    private static double Score(Action<Graphics, int, int> draw, int w = 286, int h = 160)
    {
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) draw(g, w, h);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return TextScore.Of(bytes, w, h, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [Fact]
    public void A_page_of_text_scores_as_text()
    {
        double page = Score((g, w, h) =>
        {
            g.Clear(Color.White);
            using var font = new Font("Segoe UI", 10);
            for (int line = 0; line < 9; line++)
                g.DrawString("The quick brown fox jumps over the lazy dog, 0123456789.", font, Brushes.Black, 4, 4 + line * 17);
        });
        Assert.True(page > MiniPlacement.Busy, $"text scored {page:0.000}");
    }

    [Fact]
    public void Flat_and_gradient_backdrops_do_not()
    {
        double flat = Score((g, _, _) => g.Clear(Color.FromArgb(46, 97, 113)));
        double gradient = Score((g, w, h) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), Color.Navy, Color.Orange, 30f);
            g.FillRectangle(brush, 0, 0, w, h);
        });
        Assert.Equal(0, flat);
        Assert.True(gradient < MiniPlacement.Busy / 4, $"gradient scored {gradient:0.000}");
    }
}

public class MiniPlacementTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0);

    private static IReadOnlyList<Spot> Spots(params (string Name, double Score)[] scored) =>
        scored.Select((s, i) => new Spot(s.Name, i * 100, 0, s.Score)).ToList();

    [Fact]
    public void Stays_put_until_text_has_been_under_it_twice()
    {
        var p = new MiniPlacement();
        var calm = Spots(("top-right", 0.0));
        Assert.Null(p.Check(T0, 0.0, atHome: true, null, () => calm)); // nothing under it
        Assert.Null(p.Check(T0.AddSeconds(1), 0.2, atHome: true, null, () => calm)); // once: a flicker
        Assert.Equal("top-right", p.Check(T0.AddSeconds(2), 0.2, atHome: true, null, () => calm)?.Name);
    }

    [Fact]
    public void Goes_only_somewhere_clearly_calmer()
    {
        var p = new MiniPlacement();
        var busyEverywhere = Spots(("top", 0.15), ("bottom", 0.12));
        p.Check(T0, 0.2, atHome: true, null, () => busyEverywhere);
        Assert.Null(p.Check(T0.AddSeconds(1), 0.2, atHome: true, null, () => busyEverywhere)); // nowhere calm: stay
        var oneCalm = Spots(("top", 0.15), ("bottom", 0.01));
        Assert.Equal("bottom", p.Check(T0.AddSeconds(2), 0.2, atHome: true, null, () => oneCalm)?.Name);
    }

    [Fact]
    public void Waits_ten_seconds_between_moves()
    {
        var p = new MiniPlacement();
        var calm = Spots(("left", 0.0));
        p.Check(T0, 0.3, atHome: true, null, () => calm);
        Assert.NotNull(p.Check(T0.AddSeconds(1), 0.3, atHome: true, null, () => calm));
        for (int s = 2; s < 11; s++) Assert.Null(p.Check(T0.AddSeconds(s), 0.3, atHome: false, null, () => calm));
        Assert.NotNull(p.Check(T0.AddSeconds(12), 0.3, atHome: false, null, () => calm));
    }

    [Fact]
    public void Goes_back_to_its_favorite_spot_as_soon_as_it_is_clear()
    {
        var p = new MiniPlacement();
        var home = new Spot("home", 5, 5, 0.01);
        var busyHome = home with { Score = 0.3 };
        p.Check(T0, 0.3, atHome: true, null, () => Spots(("right", 0.0)));
        Assert.Equal("right", p.Check(T0.AddSeconds(1), 0.3, atHome: true, null, () => Spots(("right", 0.0)))?.Name);
        Assert.Null(p.Check(T0.AddSeconds(12), 0.0, atHome: false, busyHome, () => [])); // home still has text
        Assert.Null(p.Check(T0.AddSeconds(13), 0.0, atHome: false, home, () => []));     // clear once: could be a flicker
        Assert.Equal("home", p.Check(T0.AddSeconds(14), 0.0, atHome: false, home, () => [])?.Name); // clear twice: back
    }

    [Fact]
    public void Never_twice_within_ten_seconds_even_to_go_back()
    {
        var p = new MiniPlacement();
        var home = new Spot("home", 5, 5, 0.0);
        p.Check(T0, 0.3, atHome: true, null, () => Spots(("right", 0.0)));
        Assert.NotNull(p.Check(T0.AddSeconds(1), 0.3, atHome: true, null, () => Spots(("right", 0.0))));
        for (int s = 2; s < 11; s++) Assert.Null(p.Check(T0.AddSeconds(s), 0.0, atHome: false, home, () => []));
        Assert.Equal("home", p.Check(T0.AddSeconds(11), 0.0, atHome: false, home, () => [])?.Name);
    }

    [Fact]
    public void A_drag_holds_it_where_it_was_dropped_for_ten_seconds()
    {
        var p = new MiniPlacement();
        var home = new Spot("home", 5, 5, 0.0);
        p.Dragged(T0);
        for (int s = 1; s < 10; s++) Assert.Null(p.Check(T0.AddSeconds(s), 0.0, atHome: false, home, () => []));
        Assert.Equal("home", p.Check(T0.AddSeconds(10), 0.0, atHome: false, home, () => [])?.Name);
    }

    [Fact]
    public void Favorite_spots_are_found_by_name()
    {
        Assert.Equal((2260, 1208), MiniPlacement.Named("bottom-right", 0, 0, 2560, 1392, 284, 168));
        Assert.Equal((16, 16), MiniPlacement.Named("top-left", 0, 0, 2560, 1392, 284, 168));
        Assert.Null(MiniPlacement.Named("custom", 0, 0, 2560, 1392, 284, 168));
        Assert.Equal(MiniPlacement.Names, MiniPlacement.Candidates(0, 0, 2560, 1392, 284, 168).Select(c => c.Name));
    }

    [Fact]
    public void Spots_over_where_it_is_or_its_favorite_are_left_out()
    {
        // A real case: put 8 px from the corner, where the bottom-right spot (16 px in) covered nearly all of it.
        var spots = MiniPlacement.Candidates(0, 0, 2560, 1392, 284, 168, (2268, 1216));
        Assert.DoesNotContain(spots, s => s.Name == "bottom-right");
        Assert.Equal(7, spots.Count);
        // Away at the top-left with home at the bottom-right: neither is offered.
        spots = MiniPlacement.Candidates(0, 0, 2560, 1392, 284, 168, (16, 16), (2268, 1216));
        Assert.Equal(new[] { "top", "top-right", "right", "bottom", "bottom-left", "left" }, spots.Select(s => s.Name));
    }

    [Fact]
    public void Candidates_sit_inside_the_work_area()
    {
        var spots = MiniPlacement.Candidates(0, 0, 2560, 1400, 300, 180);
        Assert.Equal(8, spots.Count);
        Assert.All(spots, s => Assert.True(s.X >= 16 && s.Y >= 16 && s.X + 300 <= 2544 && s.Y + 180 <= 1384));
    }
}
