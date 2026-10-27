using System.Drawing;

namespace Lighthouse.Core.Rendering;

/// <summary>A screen as the mini sees it (device pixels): its whole area and the part outside the taskbar.</summary>
public sealed record ScreenArea(Rectangle Bounds, Rectangle WorkingArea, bool Primary);

/// <summary>What the mini does about a fullscreen window this check.</summary>
public enum FullscreenStepKind { Nothing, Hide, MoveTo, Show }

/// <summary>One step: hide, move to (X, Y) (device pixels, top-left), show again where it was, or nothing.</summary>
public sealed record FullscreenStep(FullscreenStepKind Kind, int X = 0, int Y = 0)
{
    public static readonly FullscreenStep Nothing = new(FullscreenStepKind.Nothing);
}

/// <summary>
/// The mini gets out of the way of a fullscreen video or game. Pure rules, so they are tested: while a fullscreen
/// window covers the mini's screen, the mini moves to the same favorite spot on another screen ("move", the
/// default) or hides when there is no other screen ("hide" always hides). When fullscreen ends it goes back where it
/// was, or shows again. "stay" leaves it on top. A drag while it is away leaves it where it was dropped.
/// </summary>
public sealed class MiniFullscreen
{
    /// <summary>The settings' choices, in menu order.</summary>
    public static readonly string[] Modes = ["move", "hide", "stay"];

    private enum Away { No, Moved, Hidden }

    private Away _away;
    private Point _home;
    private bool _leftAlone;

    /// <summary>True while it is hidden for a fullscreen window.</summary>
    public bool Hidden => _away == Away.Hidden;

    /// <summary>True while it is moved or hidden for a fullscreen window: its own placement rules wait.</summary>
    public bool IsAway => _away != Away.No;

    /// <summary>
    /// One check. <paramref name="fullscreen"/> is the screen a fullscreen window covers (null for none),
    /// <paramref name="mini"/> where the mini is, <paramref name="favorite"/> its favorite spot's name.
    /// </summary>
    public FullscreenStep Check(Rectangle? fullscreen, Rectangle mini, IReadOnlyList<ScreenArea> screens, string mode, string favorite)
    {
        if (fullscreen is not { } full || mode == "stay") return Back();
        if (_leftAlone) return FullscreenStep.Nothing;
        if (_away == Away.Hidden || !full.IntersectsWith(mini)) return FullscreenStep.Nothing;
        // The mini is on the fullscreen window's screen: somewhere else, or out of sight.
        if (_away == Away.No) _home = mini.Location;
        var other = mode != "hide" ?screens.Where(s => !s.Bounds.IntersectsWith(full)).OrderByDescending(s => s.Primary).FirstOrDefault() : null;
        if (other is null)
        {
            _away = Away.Hidden;
            return new FullscreenStep(FullscreenStepKind.Hide);
        }
        var area = other.WorkingArea;
        var spot = MiniPlacement.Named(favorite, area.Left, area.Top, area.Right, area.Bottom, mini.Width, mini.Height)
                   ?? MiniPlacement.Named("bottom-right", area.Left, area.Top, area.Right, area.Bottom, mini.Width, mini.Height)!.Value;
        _away = Away.Moved;
        return new FullscreenStep(FullscreenStepKind.MoveTo, spot.X, spot.Y);
    }

    /// <summary>It was dragged while away: it stays where it was dropped, and is left alone until fullscreen ends.</summary>
    public void Dragged()
    {
        if (_away == Away.Moved) _leftAlone = true;
        _away = Away.No;
    }

    /// <summary>It was opened afresh (at its favorite spot): nothing to go back to.</summary>
    public void Reset() => (_away, _leftAlone) = (Away.No, false);

    private FullscreenStep Back()
    {
        var away = _away;
        Reset();
        return away switch
        {
            Away.Moved => new FullscreenStep(FullscreenStepKind.MoveTo, _home.X, _home.Y),
            Away.Hidden => new FullscreenStep(FullscreenStepKind.Show),
            _ => FullscreenStep.Nothing,
        };
    }
}
