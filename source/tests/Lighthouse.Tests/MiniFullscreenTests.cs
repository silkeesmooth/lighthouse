using System.Drawing;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

public class MiniFullscreenTests
{
    // A 2560x1440 main screen with a 48 px taskbar, and a 1920x1080 one to its right.
    private static readonly ScreenArea Main = new(new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1392), true);
    private static readonly ScreenArea Second = new(new Rectangle(2560, 0, 1920, 1080), new Rectangle(2560, 0, 1920, 1032), false);
    private static readonly Rectangle Mini = new(2268, 1216, 286, 160); // bottom right of the main screen

    [Fact]
    public void Nothing_happens_without_fullscreen()
    {
        var rules = new MiniFullscreen();
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(null, Mini, [Main, Second], "move", "bottom-right").Kind);
        Assert.False(rules.IsAway);
    }

    [Fact]
    public void One_screen_hides_then_shows_again()
    {
        var rules = new MiniFullscreen();
        Assert.Equal(FullscreenStepKind.Hide, rules.Check(Main.Bounds, Mini, [Main], "move", "bottom-right").Kind);
        Assert.True(rules.Hidden);
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(Main.Bounds, Mini, [Main], "move", "bottom-right").Kind);
        Assert.Equal(FullscreenStepKind.Show, rules.Check(null, Mini, [Main], "move", "bottom-right").Kind);
        Assert.False(rules.IsAway);
    }

    [Fact]
    public void Two_screens_move_to_the_same_spot_on_the_other_and_back()
    {
        var rules = new MiniFullscreen();
        var away = rules.Check(Main.Bounds, Mini, [Main, Second], "move", "bottom-right");
        Assert.Equal(new FullscreenStep(FullscreenStepKind.MoveTo, 2560 + 1920 - 286 - 16, 1032 - 160 - 16), away);
        var there = Mini with { X = away.X, Y = away.Y };
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(Main.Bounds, there, [Main, Second], "move", "bottom-right").Kind);
        Assert.Equal(new FullscreenStep(FullscreenStepKind.MoveTo, Mini.X, Mini.Y), rules.Check(null, there, [Main, Second], "move", "bottom-right"));
        Assert.False(rules.IsAway);
    }

    [Fact]
    public void Fullscreen_on_the_other_screen_leaves_it_alone()
    {
        var rules = new MiniFullscreen();
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(Second.Bounds, Mini, [Main, Second], "move", "bottom-right").Kind);
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(null, Mini, [Main, Second], "move", "bottom-right").Kind);
    }

    [Fact]
    public void Prefers_the_main_screen_when_fullscreen_is_on_a_second_one()
    {
        var third = new ScreenArea(new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1032), false);
        var onSecond = Mini with { X = 3000, Y = 500 };
        var step = new MiniFullscreen().Check(Second.Bounds, onSecond, [third, Second, Main], "move", "top-left");
        Assert.Equal(new FullscreenStep(FullscreenStepKind.MoveTo, 16, 16), step);
    }

    [Fact]
    public void Hide_mode_hides_even_with_another_screen()
    {
        var rules = new MiniFullscreen();
        Assert.Equal(FullscreenStepKind.Hide, rules.Check(Main.Bounds, Mini, [Main, Second], "hide", "bottom-right").Kind);
    }

    [Fact]
    public void Stay_mode_stays_and_brings_it_back_if_switched_while_away()
    {
        var rules = new MiniFullscreen();
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(Main.Bounds, Mini, [Main], "stay", "bottom-right").Kind);
        rules.Check(Main.Bounds, Mini, [Main], "hide", "bottom-right");
        Assert.Equal(FullscreenStepKind.Show, rules.Check(Main.Bounds, Mini, [Main], "stay", "bottom-right").Kind);
    }

    [Fact]
    public void Dragged_while_away_stays_where_it_was_dropped()
    {
        var rules = new MiniFullscreen();
        var away = rules.Check(Main.Bounds, Mini, [Main, Second], "move", "bottom-right");
        rules.Dragged();
        var dropped = new Rectangle(100, 100, 286, 160); // back onto the fullscreen screen, on purpose
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(Main.Bounds, dropped, [Main, Second], "move", "bottom-right").Kind);
        Assert.Equal(FullscreenStepKind.Nothing, rules.Check(null, dropped, [Main, Second], "move", "bottom-right").Kind);
        // The next fullscreen moves it again.
        Assert.Equal(away.Kind, rules.Check(Main.Bounds, dropped, [Main, Second], "move", "bottom-right").Kind);
    }

    [Fact]
    public void A_custom_favorite_goes_to_bottom_right_of_the_other_screen()
    {
        var step = new MiniFullscreen().Check(Main.Bounds, Mini, [Main, Second], "move", "custom");
        Assert.Equal(new FullscreenStep(FullscreenStepKind.MoveTo, 2560 + 1920 - 286 - 16, 1032 - 160 - 16), step);
    }
}
