namespace SurgeLink.Terminal;

/// <summary>Visual attributes carried by a terminal cell.</summary>
[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,
}

/// <summary>Horizontal footprint of a cell, needed for East Asian wide characters.</summary>
public enum CellWidth : byte
{
    /// <summary>Single column glyph.</summary>
    Narrow = 0,

    /// <summary>First half of a two column glyph.</summary>
    Wide = 1,

    /// <summary>Right half of a two column glyph; carries no text.</summary>
    Continuation = 2,
}

/// <summary>One screen cell: text plus its resolved style.</summary>
public readonly record struct TerminalCell(
    string Text,
    TerminalColor Foreground,
    TerminalColor Background,
    CellAttributes Attributes,
    CellWidth Width)
{
    /// <summary>A blank cell using default colours.</summary>
    public static TerminalCell Blank { get; } =
        new(" ", TerminalColor.Default, TerminalColor.Default, CellAttributes.None, CellWidth.Narrow);

    /// <summary>True for the right half of a wide glyph.</summary>
    public bool IsContinuation => Width == CellWidth.Continuation;

    /// <summary>Returns a copy with different attributes.</summary>
    public TerminalCell WithAttributes(CellAttributes attributes) => this with { Attributes = attributes };

    /// <summary>Returns a copy with a different text run.</summary>
    public TerminalCell WithText(string text) => this with { Text = text };
}
