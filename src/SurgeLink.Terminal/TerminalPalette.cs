namespace SurgeLink.Terminal;

/// <summary>
/// Colour theme for the renderer. Colors are stored as 0xRRGGBB so the engine
/// stays UI framework agnostic.
/// </summary>
public sealed record TerminalPalette
{
    /// <summary>Default text colour.</summary>
    public uint Foreground { get; init; } = 0xD4D4D4;

    /// <summary>Default background colour.</summary>
    public uint Background { get; init; } = 0x0C0C0C;

    /// <summary>Cursor colour.</summary>
    public uint Cursor { get; init; } = 0xFFFFFF;

    /// <summary>Selection highlight colour.</summary>
    public uint Selection { get; init; } = 0x264F78;

    /// <summary>The 16 ANSI colours, normal 0-7 then bright 8-15.</summary>
    public IReadOnlyList<uint> Ansi { get; init; } = DefaultAnsi;

    /// <summary>Campbell style palette used when the user has not chosen a theme.</summary>
    public static IReadOnlyList<uint> DefaultAnsi { get; } = new uint[]
    {
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    };

    /// <summary>The default theme.</summary>
    public static TerminalPalette Default { get; } = new();

    /// <summary>
    /// Resolves a requested colour to a concrete 0xRRGGBB value.
    /// </summary>
    /// <param name="color">Colour requested by the application.</param>
    /// <param name="forBackground">True when resolving a background.</param>
    /// <param name="attributes">Cell attributes, used to honour bold-as-bright.</param>
    public uint Resolve(TerminalColor color, bool forBackground, CellAttributes attributes)
    {
        switch (color.Kind)
        {
            case TerminalColorKind.Rgb:
                return color.Rgb & 0xFFFFFFu;

            case TerminalColorKind.Indexed:
                return ResolveIndexed(color.Index, attributes);

            default:
                return forBackground ? Background : Foreground;
        }
    }

    /// <summary>Resolves an xterm 256 palette index to RGB.</summary>
    public uint ResolveIndexed(byte index, CellAttributes attributes = CellAttributes.None)
    {
        if (index < 16)
        {
            return Ansi[index];
        }

        if (index < 232)
        {
            var value = index - 16;
            var r = value / 36;
            var g = (value % 36) / 6;
            var b = value % 6;
            return (uint)(Level(r) << 16 | Level(g) << 8 | Level(b));

            static int Level(int component) => component == 0 ? 0 : 55 + (component * 40);
        }

        var grey = 8 + ((index - 232) * 10);
        return (uint)(grey << 16 | grey << 8 | grey);
    }
}
