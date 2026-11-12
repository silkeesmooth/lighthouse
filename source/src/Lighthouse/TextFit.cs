using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lighthouse;

/// <summary>
/// Finds text that does not fit (2026-10-22: "commit 2…" in the RAM tile). A text block is clipped when
/// its text is wider than the space it was given and it can't wrap, when a single word is wider than the space
/// it wraps in, or when the block itself runs past the window's edge. Run on every render.
/// </summary>
internal static class TextFit
{
    public static List<string> Clipped(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var problems = new List<string>();
        foreach (var block in Descendants(root).OfType<TextBlock>())
        {
            if (string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0 || block.Visibility != Visibility.Visible) continue;
            double room = block.ActualWidth - block.Padding.Left - block.Padding.Right;
            double needed = block.TextWrapping == TextWrapping.NoWrap
                ? Width(block, block.Text)
                : block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(word => Width(block, word));
            if (needed > room + 0.5)
                problems.Add($"'{block.Text}' needs {needed:0} px, has {room:0}");
            // Text that won't fit gets its full width anyway and is clipped to the space its parent gave it, or its parent
            // is, so the block alone looks fine (2026-11-12: "Driver stopped" in a gauge tile and under the mini's gauge).
            if (NarrowerSlot(block, root) is { } slot)
                problems.Add($"'{block.Text}' needs {slot.Needed:0} px, has {slot.Has:0}");
            var bounds = block.TransformToAncestor(root).TransformBounds(new Rect(block.RenderSize));
            if (bounds.Right > root.ActualWidth + 0.5 || bounds.Left < -0.5)
                problems.Add($"'{block.Text}' runs past the window edge");
        }
        return problems;
    }

    /// <summary>The block or a parent of it that is wider than the slot it was laid out in, or null.</summary>
    private static (double Needed, double Has)? NarrowerSlot(FrameworkElement block, FrameworkElement root)
    {
        for (DependencyObject? e = block; e is not null && e != root; e = VisualTreeHelper.GetParent(e))
        {
            if (e is not FrameworkElement { IsArrangeValid: true } element || element is ScrollContentPresenter) continue;
            double slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element).Width;
            double width = element.RenderSize.Width + element.Margin.Left + element.Margin.Right;
            if (slot > 0 && width > slot + 0.5) return (width, slot);
        }
        return null;
    }

    private static double Width(TextBlock block, string text) =>
        new FormattedText(text, CultureInfo.CurrentCulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue; // not drawn, so nothing to cut off
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }
}
