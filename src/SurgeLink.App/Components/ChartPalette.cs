using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Services;

namespace SurgeLink.App.Components;

/// <summary>
/// Theme aware brushes for charts, gauges and usage bars. Every brush is looked
/// up from a WinUI theme resource on each render, so light, dark and High
/// Contrast resolve to system colours instead of hard coded values.
/// </summary>
internal static class ChartPalette
{
    private const string AccentKey = "AccentFillColorDefaultBrush";
    private const string TrackKey = "ControlFillColorSecondaryBrush";
    private const string GridLineKey = "DividerStrokeColorDefaultBrush";
    private const string PrimaryKey = "TextFillColorPrimaryBrush";
    private const string SecondaryKey = "TextFillColorSecondaryBrush";
    private const string SuccessKey = "SystemFillColorSuccessBrush";
    private const string CautionKey = "SystemFillColorCautionBrush";
    private const string CriticalKey = "SystemFillColorCriticalBrush";

    /// <summary>Theme reference for the accent brush.</summary>
    public static ThemeRef AccentRef { get; } = new(AccentKey);

    /// <summary>Theme reference for the unfilled track of a bar or gauge.</summary>
    public static ThemeRef TrackRef { get; } = new(TrackKey);

    /// <summary>Theme reference for chart gridlines.</summary>
    public static ThemeRef GridLineRef { get; } = new(GridLineKey);

    /// <summary>Theme reference for primary text/stroke.</summary>
    public static ThemeRef PrimaryRef { get; } = new(PrimaryKey);

    /// <summary>Theme reference for secondary text/stroke.</summary>
    public static ThemeRef SecondaryRef { get; } = new(SecondaryKey);

    /// <summary>Theme reference for success status.</summary>
    public static ThemeRef SuccessRef { get; } = new(SuccessKey);

    /// <summary>Theme reference for caution status.</summary>
    public static ThemeRef CautionRef { get; } = new(CautionKey);

    /// <summary>Theme reference for critical status.</summary>
    public static ThemeRef CriticalRef { get; } = new(CriticalKey);

    /// <summary>Accent brush, used for the primary series and healthy usage.</summary>
    public static Brush Accent => ThemeResource.Brush(AccentKey);

    /// <summary>Subtle fill used for the unfilled part of a bar or gauge.</summary>
    public static Brush Track => ThemeResource.Brush(TrackKey);

    /// <summary>Divider brush used for chart gridlines.</summary>
    public static Brush GridLine => ThemeResource.Brush(GridLineKey);

    /// <summary>Primary text brush.</summary>
    public static Brush Primary => ThemeResource.Brush(PrimaryKey);

    /// <summary>Secondary text brush.</summary>
    public static Brush Secondary => ThemeResource.Brush(SecondaryKey);

    /// <summary>Success (green) system brush.</summary>
    public static Brush Success => ThemeResource.Brush(SuccessKey);

    /// <summary>Caution (yellow) system brush.</summary>
    public static Brush Caution => ThemeResource.Brush(CautionKey);

    /// <summary>Critical (red) system brush.</summary>
    public static Brush Critical => ThemeResource.Brush(CriticalKey);

    /// <summary>Theme reference for series <paramref name="index"/> in a multi-series chart.</summary>
    public static ThemeRef SeriesRef(int index) => index switch
    {
        0 => AccentRef,
        1 => SuccessRef,
        2 => CautionRef,
        _ => SecondaryRef,
    };

    /// <summary>Theme reference mapping a 0..1 usage fraction to accent, caution or critical.</summary>
    public static ThemeRef SeverityRef(double fraction) =>
        fraction >= 0.9 ? CriticalRef : fraction >= 0.75 ? CautionRef : AccentRef;

    /// <summary>Resolves gridline brush for <paramref name="element"/>'s effective theme.</summary>
    public static Brush ResolveGridLine(FrameworkElement element, bool highContrast = false) =>
        Resolve(GridLineKey, element, highContrast);

    /// <summary>Resolves gauge/bar track brush for <paramref name="element"/>'s effective theme.</summary>
    public static Brush ResolveTrack(FrameworkElement element, bool highContrast = false) =>
        Resolve(TrackKey, element, highContrast);

    /// <summary>Resolves secondary text/stroke brush for <paramref name="element"/>'s effective theme.</summary>
    public static Brush ResolveSecondary(FrameworkElement? element, bool highContrast = false) =>
        Resolve(SecondaryKey, element, highContrast);

    /// <summary>Resolves series stroke brush for <paramref name="element"/>'s effective theme.</summary>
    public static Brush ResolveSeries(int index, FrameworkElement element, bool highContrast = false) =>
        Resolve(index switch
        {
            0 => AccentKey,
            1 => SuccessKey,
            2 => CautionKey,
            _ => SecondaryKey,
        }, element, highContrast);

    /// <summary>Resolves severity brush for <paramref name="element"/>'s effective theme.</summary>
    public static Brush ResolveSeverity(double fraction, FrameworkElement element, bool highContrast = false) =>
        Resolve(fraction >= 0.9 ? CriticalKey : fraction >= 0.75 ? CautionKey : AccentKey, element, highContrast);

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

    private static Brush Resolve(string key, FrameworkElement? element, bool highContrast)
    {
        if (highContrast)
        {
            return ThemeResource.Brush(key);
        }

        bool isDark = AppServices.Current?.Settings.Current.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => element?.ActualTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
            },
        };

        return ThemeRef.Resolve(key, isDark) as Brush ?? ThemeResource.Brush(key);
    }
}
