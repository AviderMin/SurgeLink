using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;

namespace SurgeLink.App.Components;

/// <summary>
/// Theme aware brushes for charts, gauges and usage bars. Every brush is looked
/// up from a WinUI theme resource on each render, so light, dark and High
/// Contrast resolve to system colours instead of hard coded values. Charts call
/// <c>UseColorScheme()</c> so a theme change repaints them.
/// </summary>
internal static class ChartPalette
{
    /// <summary>Accent brush, used for the primary series and healthy usage.</summary>
    public static Brush Accent => ThemeResource.Brush("AccentFillColorDefaultBrush");

    /// <summary>Subtle fill used for the unfilled part of a bar or gauge.</summary>
    public static Brush Track => ThemeResource.Brush("ControlFillColorSecondaryBrush");

    /// <summary>Divider brush used for chart gridlines.</summary>
    public static Brush GridLine => ThemeResource.Brush("DividerStrokeColorDefaultBrush");

    /// <summary>Primary text brush.</summary>
    public static Brush Primary => ThemeResource.Brush("TextFillColorPrimaryBrush");

    /// <summary>Secondary text brush.</summary>
    public static Brush Secondary => ThemeResource.Brush("TextFillColorSecondaryBrush");

    /// <summary>Success (green) system brush.</summary>
    public static Brush Success => ThemeResource.Brush("SystemFillColorSuccessBrush");

    /// <summary>Caution (yellow) system brush.</summary>
    public static Brush Caution => ThemeResource.Brush("SystemFillColorCautionBrush");

    /// <summary>Critical (red) system brush.</summary>
    public static Brush Critical => ThemeResource.Brush("SystemFillColorCriticalBrush");

    /// <summary>Picks the stroke for series <paramref name="index"/> in a multi series chart.</summary>
    public static Brush Series(int index) => index switch
    {
        0 => Accent,
        1 => Success,
        2 => Caution,
        _ => Secondary,
    };

    /// <summary>Maps a 0..1 usage fraction to a status colour: accent, caution, critical.</summary>
    public static Brush Severity(double fraction) =>
        fraction >= 0.9 ? Critical : fraction >= 0.75 ? Caution : Accent;
}
