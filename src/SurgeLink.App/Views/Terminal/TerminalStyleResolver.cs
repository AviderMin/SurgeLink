using Microsoft.UI.Xaml.Media;
using SurgeLink.Terminal;
using Windows.UI;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Turns a cell's requested colours into concrete RGB values and reuses one
/// <see cref="SolidColorBrush"/> per colour, so a repaint allocates no brushes.
/// The terminal palette is not a theme token: it is the user chosen colour scheme
/// (see <see cref="TerminalThemes"/>) and must be used verbatim.
/// </summary>
internal sealed class TerminalStyleResolver
{
    // True-colour output can mention millions of distinct colours. A hard cap
    // keeps one long session from pinning an unbounded number of XAML brushes.
    private const int MaximumCachedBrushes = 4096;

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    /// <summary>Creates a resolver for a palette.</summary>
    public TerminalStyleResolver(TerminalPalette palette)
    {
        Palette = palette;
        DefaultBackground = palette.Background & 0xFFFFFFu;
        DefaultForeground = palette.Foreground & 0xFFFFFFu;
        SelectionBackground = palette.Selection & 0xFFFFFFu;
        Cursor = palette.Cursor & 0xFFFFFFu;

        // Search highlights are derived from the terminal palette rather than a
        // shell theme token so they stay legible on every terminal colour scheme.
        // Dark backgrounds get a warm low-luminance band; light backgrounds get a
        // pale amber band. The current match is always stronger than the others.
        bool darkBackground = Brightness(palette.Background) < 128000;
        SearchBackground = Blend(0xFFD700u, palette.Background, darkBackground ? 0.70 : 0.65);
        SearchCurrentBackground = Blend(darkBackground ? 0xFFD700u : 0xFFB300u, palette.Background, darkBackground ? 0.40 : 0.45);
    }

    /// <summary>The palette this resolver resolves against.</summary>
    public TerminalPalette Palette { get; }

    /// <summary>RGB used for cells that ask for the theme background.</summary>
    public uint DefaultBackground { get; }

    /// <summary>RGB used for cells that ask for the theme foreground.</summary>
    public uint DefaultForeground { get; }

    /// <summary>RGB of the selection highlight.</summary>
    public uint SelectionBackground { get; }

    /// <summary>RGB of the cursor block, bar or underline.</summary>
    public uint Cursor { get; }

    /// <summary>RGB of a search match that is not the current one.</summary>
    public uint SearchBackground { get; }

    /// <summary>RGB of the search match the user is currently on.</summary>
    public uint SearchCurrentBackground { get; }

    /// <summary>Resolves a cell into paintable colours, applying inverse and dim.</summary>
    public TerminalCellStyle Resolve(in TerminalCell cell)
    {
        CellAttributes attributes = cell.Attributes;
        uint foreground = Palette.Resolve(cell.Foreground, forBackground: false, attributes);
        uint background = Palette.Resolve(cell.Background, forBackground: true, attributes);

        if ((attributes & CellAttributes.Dim) != 0)
        {
            foreground = Blend(foreground, background, 0.5);
        }

        if ((attributes & CellAttributes.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        uint underline = cell.UnderlineColor.Kind == TerminalColorKind.Default
            ? foreground
            : Palette.Resolve(cell.UnderlineColor, forBackground: false, CellAttributes.None);
        return new TerminalCellStyle(foreground & 0xFFFFFFu, background & 0xFFFFFFu, attributes, underline & 0xFFFFFFu);
    }

    /// <summary>Returns a cached brush for an 0xRRGGBB colour.</summary>
    public SolidColorBrush Brush(uint rgb)
    {
        if (_brushes.TryGetValue(rgb, out var brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(ToColor(rgb));
        if (_brushes.Count >= MaximumCachedBrushes)
        {
            _brushes.Clear();
        }

        _brushes[rgb] = brush;
        return brush;
    }

    /// <summary>Converts 0xRRGGBB to an opaque XAML colour.</summary>
    public static Color ToColor(uint rgb) => Color.FromArgb(
        255,
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));

    private static int Brightness(uint rgb)
    {
        int red = (int)((rgb >> 16) & 0xFF);
        int green = (int)((rgb >> 8) & 0xFF);
        int blue = (int)(rgb & 0xFF);
        return (red * 299) + (green * 587) + (blue * 114);
    }

    private static uint Blend(uint foreground, uint background, double amount)
    {
        // Fast integer mix: amount is a constant 0.5 for dim text.
        int fgR = (int)((foreground >> 16) & 0xFF);
        int fgG = (int)((foreground >> 8) & 0xFF);
        int fgB = (int)(foreground & 0xFF);
        int bgR = (int)((background >> 16) & 0xFF);
        int bgG = (int)((background >> 8) & 0xFF);
        int bgB = (int)(background & 0xFF);

        uint r = (uint)(fgR + ((bgR - fgR) * amount));
        uint g = (uint)(fgG + ((bgG - fgG) * amount));
        uint b = (uint)(fgB + ((bgB - fgB) * amount));
        return (r << 16) | (g << 8) | b;
    }
}
