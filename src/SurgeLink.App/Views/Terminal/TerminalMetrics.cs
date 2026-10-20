using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Styling;
using Windows.Foundation;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Cell metrics for the terminal's monospace font, measured once per font/size.
/// </summary>
/// <remarks>
/// Text runs are positioned on a grid of <see cref="CellWidth"/> x <see cref="CellHeight"/>
/// DIPs. The width is derived from the font's real advance and deliberately never
/// rounded: a run draws its glyphs with the font's natural advance, so rounding the
/// cell width would make every neighbouring run drift by up to half a pixel per cell.
/// The advance is measured as the difference between two sample strings so any
/// constant padding in the text layout cancels out.
/// </remarks>
internal sealed class TerminalMetrics
{
    private const int ShortSampleLength = 16;
    private const int LongSampleLength = 48;

    private TerminalMetrics(FontFamily fontFamily, string fontFamilyName, double fontSize, double cellWidth, double cellHeight)
    {
        FontFamily = fontFamily;
        FontFamilyName = fontFamilyName;
        FontSize = fontSize;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
    }

    /// <summary>Font family the metrics were measured for.</summary>
    public FontFamily FontFamily { get; }

    /// <summary>Name of the measured family, kept so callers can compare settings.</summary>
    public string FontFamilyName { get; }

    /// <summary>Font size in DIPs.</summary>
    public double FontSize { get; }

    /// <summary>Advance of one narrow cell in DIPs.</summary>
    public double CellWidth { get; }

    /// <summary>Line height of one row in DIPs.</summary>
    public double CellHeight { get; }

    /// <summary>Measures a font/size pair, falling back to typical ratios when text layout gives nothing usable.</summary>
    public static TerminalMetrics Measure(string? fontFamilyName, double fontSize)
    {
        string name = string.IsNullOrWhiteSpace(fontFamilyName) ? AppTheme.MonospaceFamilyName : fontFamilyName;
        double size = fontSize > 0 ? fontSize : 13;
        var family = new FontFamily(name);

        double fallbackWidth = size * 0.6;
        double fallbackHeight = Math.Ceiling(size * 1.35);

        try
        {
            var shortProbe = CreateProbe(family, size, ShortSampleLength);
            shortProbe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var longProbe = CreateProbe(family, size, LongSampleLength);
            longProbe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double width = (longProbe.DesiredSize.Width - shortProbe.DesiredSize.Width) / (LongSampleLength - ShortSampleLength);
            double height = shortProbe.DesiredSize.Height;
            return new TerminalMetrics(
                family,
                name,
                size,
                IsUsable(width) ? width : fallbackWidth,
                IsUsable(height) ? height : fallbackHeight);
        }
        catch (Exception)
        {
            // A missing font must never take the terminal down; fall back to ratios.
            return new TerminalMetrics(family, name, size, fallbackWidth, fallbackHeight);
        }
    }

    private static TextBlock CreateProbe(FontFamily family, double size, int count) => new()
    {
        FontFamily = family,
        FontSize = size,
        IsTextScaleFactorEnabled = false,
        TextWrapping = TextWrapping.NoWrap,
        Text = new string('M', count),
    };

    private static bool IsUsable(double value) =>
        value > 0.1 && !double.IsNaN(value) && !double.IsInfinity(value);
}
