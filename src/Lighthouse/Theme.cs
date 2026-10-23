using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lighthouse;

/// <summary>Lighthouse's palette for the flyout and history windows, following Windows' app light/dark setting.</summary>
public static class Theme
{
    public static bool AppsLight => ReadFlag("AppsUseLightTheme");
    public static bool TaskbarLight => ReadFlag("SystemUsesLightTheme");

    private static readonly Dictionary<string, (string Light, string Dark)> Colors = new()
    {
        ["Bg"] = ("#fafcfa", "#172327"),
        ["Surface"] = ("#edf3f5", "#1f3238"),
        ["Ink"] = ("#1d2c31", "#edf3f5"),
        ["Muted"] = ("#506068", "#9fb3ba"),
        ["Line"] = ("#d9e5d6", "#2b4249"),
        ["Accent"] = ("#2e6171", "#7fb3c2"),
        ["Good"] = ("#5b7553", "#8fb386"),
        ["Warn"] = ("#b8962e", "#f1e38f"),
        ["Hot"] = ("#c4483a", "#e26d5c"),
        ["ButtonBg"] = ("#2e6171", "#2e6171"),
        ["ButtonFg"] = ("#fafcfa", "#edf3f5"),
        ["Series2"] = ("#b8962e", "#f1e38f"),
        ["Web"] = ("#4f7cac", "#8fb3d9"),
    };

    /// <summary>The flyout's Dark mode switch: true light, false dark, null follows Windows.</summary>
    public static bool? Chosen { get; set; }

    /// <summary>Whether the palette now in use is the light one.</summary>
    public static bool IsLight { get; private set; } = true;

    /// <summary>The chosen theme, else Windows' app theme; or the one asked for (the README pictures draw both).</summary>
    public static void Apply(bool? light = null)
    {
        bool isLight = light ?? Chosen ?? AppsLight;
        IsLight = isLight;
        foreach (var (key, (lightHex, darkHex)) in Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(isLight ? lightHex : darkHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
            Application.Current.Resources[key + "Color"] = color; // for effects, which take a Color
        }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static readonly Dictionary<(string, bool), Brush> ForBackdrop = [];

    /// <summary>
    /// A palette colour for a light or a dark background, whatever the app theme is: the mini monitor sits on
    /// the desktop, so it picks by what is behind it.
    /// </summary>
    public static Brush BrushFor(string key, bool darkBackground)
    {
        if (ForBackdrop.TryGetValue((key, darkBackground), out var cached)) return cached;
        var (lightHex, darkHex) = Colors[key];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(darkBackground ? darkHex : lightHex));
        brush.Freeze();
        return ForBackdrop[(key, darkBackground)] = brush;
    }

    private static bool ReadFlag(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue(name) is not int v || v != 0;
    }
}
