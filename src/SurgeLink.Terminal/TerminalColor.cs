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
    private readonly uint _packed;
    private const uint ExplicitIndex = 1u << 26;
    private TerminalColor(uint packed) => _packed = packed;
    /// <summary>Default theme foreground or background.</summary>
    public static TerminalColor Default { get; } = default;

    /// <summary>Colour kind.</summary>
    public TerminalColorKind Kind => (TerminalColorKind)((_packed >> 24) & 3);

    /// <summary>Palette index, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Indexed"/>.</summary>
    public byte Index => Kind == TerminalColorKind.Indexed ? (byte)_packed : (byte)0;

    /// <summary>True when an extended SGR requested this exact palette index.</summary>
    public bool IsExplicitIndex => (_packed & ExplicitIndex) != 0;

    /// <summary>0xRRGGBB value, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public uint Rgb => Kind == TerminalColorKind.Rgb ? _packed & 0xFFFFFFu : 0;

    /// <summary>Creates an indexed colour.</summary>
    public static TerminalColor FromIndex(byte index) => FromIndex(index, false);

    /// <summary>Creates an indexed colour with optional exact-index semantics.</summary>
    public static TerminalColor FromIndex(byte index, bool isExplicit) =>
        new(((uint)TerminalColorKind.Indexed << 24) | index | (isExplicit ? ExplicitIndex : 0));

    /// <summary>Creates a true colour value.</summary>
    public static TerminalColor FromRgb(uint rgb) => new(((uint)TerminalColorKind.Rgb << 24) | (rgb & 0xFFFFFFu));

    /// <summary>Same colour with a different palette index.</summary>
    public TerminalColor WithIndex(byte index) => FromIndex(index, IsExplicitIndex);
}
