using System.ComponentModel;
using System.Windows.Media;

namespace Lighthouse;

/// <summary>
/// One gauge in the mini monitor: the shared tile's numbers, drawn in white or black (and the arc in its dark-
/// or light-background shade) depending on what is behind that gauge on the desktop.
/// </summary>
public sealed class MiniTile : INotifyPropertyChanged
{
    private static readonly Brush White = Frozen(Color.FromRgb(255, 255, 255));
    private static readonly Brush Black = Frozen(Color.FromRgb(17, 17, 17));
    private static readonly Brush WhiteTrack = Frozen(Color.FromArgb(70, 255, 255, 255));
    private static readonly Brush BlackTrack = Frozen(Color.FromArgb(40, 0, 0, 0));

    public MiniTile(TileVm tile)
    {
        Tile = tile;
        tile.PropertyChanged += (_, _) => Raise();
    }

    public TileVm Tile { get; }

    /// <summary>True when the desktop behind this gauge (seen through the panel) is dark.</summary>
    public bool OnDark { get; private set; }

    public Brush Text => OnDark ? White : Black;
    public Brush Track => OnDark ? WhiteTrack : BlackTrack;
    public Brush Arc => Theme.BrushFor(Tile.AccentKey, OnDark);

    public void SetBackdrop(bool onDark)
    {
        if (onDark == OnDark) return;
        OnDark = onDark;
        Raise();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
