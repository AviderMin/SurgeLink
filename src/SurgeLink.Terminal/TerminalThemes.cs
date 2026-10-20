namespace SurgeLink.Terminal;

/// <summary>Ready made colour schemes offered in settings.</summary>
public static class TerminalThemes
{
    /// <summary>The Windows Terminal default (Campbell).</summary>
    public static TerminalPalette Campbell { get; } = new();

    /// <summary>Dark blue tinted scheme.</summary>
    public static TerminalPalette SolarizedDark { get; } = new()
    {
        Foreground = 0x839496,
        Background = 0x002B36,
        Cursor = 0x93A1A1,
        Selection = 0x0F4B56,
        Ansi = new uint[]
        {
            0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
            0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3,
        },
    };

    /// <summary>High contrast light scheme.</summary>
    public static TerminalPalette Light { get; } = new()
    {
        Foreground = 0x1B1B1B,
        Background = 0xFFFFFF,
        Cursor = 0x000000,
        Selection = 0xCCE8FF,
        Ansi = new uint[]
        {
            0x000000, 0xC50F1F, 0x107C10, 0x9A6600, 0x0037DA, 0x881798, 0x3A96DD, 0x767676,
            0x808080, 0xE74856, 0x13A10E, 0xC19C00, 0x3B78FF, 0xB4009E, 0x61D6D6, 0x000000,
        },
    };

    /// <summary>Named schemes available in the settings page.</summary>
    public static IReadOnlyList<(string Name, TerminalPalette Palette)> All { get; } = new[]
    {
        ("Campbell", Campbell),
        ("Solarized Dark", SolarizedDark),
        ("Light", Light),
    };
}
