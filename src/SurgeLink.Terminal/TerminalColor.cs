namespace SurgeLink.Terminal;

/// <summary>How a <see cref="TerminalColor"/> was specified by the application on the remote side.</summary>
public enum TerminalColorKind : byte
{
    /// <summary>Use the theme default (foreground or background).</summary>
    Default = 0,

    /// <summary>An index into the 256 colour xterm palette.</summary>
    Indexed = 1,

    /// <summary>A 24 bit RGB value.</summary>
    Rgb = 2,
}

/// <summary>
/// A colour as requested by the remote program; resolution against a
/// <see cref="TerminalPalette"/> happens at paint time so a theme switch does
/// not require re-parsing the output.
/// </summary>
public readonly record struct TerminalColor
{
    /// <summary>Default theme foreground or background.</summary>
    public static TerminalColor Default { get; } = default;

    /// <summary>Colour kind.</summary>
    public TerminalColorKind Kind { get; init; }

    /// <summary>Palette index, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Indexed"/>.</summary>
    public byte Index { get; init; }

    /// <summary>0xRRGGBB value, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public uint Rgb { get; init; }

    /// <summary>Creates an indexed colour.</summary>
    public static TerminalColor FromIndex(byte index) => new() { Kind = TerminalColorKind.Indexed, Index = index };

    /// <summary>Creates a true colour value.</summary>
    public static TerminalColor FromRgb(uint rgb) => new() { Kind = TerminalColorKind.Rgb, Rgb = rgb & 0xFFFFFFu };

    /// <summary>Same colour with a different palette index.</summary>
    public TerminalColor WithIndex(byte index) => this with { Kind = TerminalColorKind.Indexed, Index = index, Rgb = 0 };
}
