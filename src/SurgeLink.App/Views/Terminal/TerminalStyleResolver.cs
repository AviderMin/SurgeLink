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
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    /// <summary>Creates a resolver for a palette.</summary>
    public TerminalStyleResolver(TerminalPalette palette)
    {
        Palette = palette;
        DefaultBackground = palette.Background & 0xFFFFFFu;
        DefaultForeground = palette.Foreground & 0xFFFFFFu;
        SelectionBackground = palette.Selection & 0xFFFFFFu;
        Cursor = palette.Cursor & 0xFFFFFFu;
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

    /// <summary>Resolves a cell into paintable colours, applying inverse and dim.</summary>
    public TerminalCellStyle Resolve(in TerminalCell cell)
    {
        CellAttributes attributes = cell.Attributes;
        uint foreground = Palette.Resolve(cell.Foreground, forBackground: false, attributes);
        uint background = Palette.Resolve(cell.Background, forBackground: true, attributes);

        // Classic xterm behaviour: bold promotes the eight base colours to their
        // bright counterparts. True colour and 256 colour cells keep their own value.
        if ((attributes & CellAttributes.Bold) != 0
            && cell.Foreground.Kind == TerminalColorKind.Indexed
            && cell.Foreground.Index < 8)
        {
            foreground = Palette.Ansi[cell.Foreground.Index + 8];
        }

        if ((attributes & CellAttributes.Dim) != 0)
        {
            foreground = Blend(foreground, background, 0.5);
        }

        if ((attributes & CellAttributes.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        return new TerminalCellStyle(foreground & 0xFFFFFFu, background & 0xFFFFFFu, attributes);
    }

    /// <summary>Returns a cached brush for an 0xRRGGBB colour.</summary>
    public SolidColorBrush Brush(uint rgb)
    {
        if (_brushes.TryGetValue(rgb, out var brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(ToColor(rgb));
        _brushes[rgb] = brush;
        return brush;
    }

    /// <summary>Converts 0xRRGGBB to an opaque XAML colour.</summary>
    public static Color ToColor(uint rgb) => Color.FromArgb(
        255,
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));

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
