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
public readonly record struct TerminalCell
{
    // Most cells store a string directly. A link only adds an object for cells that use one.
    private readonly object? _content;
    private sealed record LinkedText(string Text, string Hyperlink);

    public TerminalCell(string Text, TerminalColor Foreground, TerminalColor Background,
        CellAttributes Attributes, CellWidth Width, string? Hyperlink = null, TerminalColor UnderlineColor = default)
    {
        _content = Hyperlink is null ? Text : new LinkedText(Text, Hyperlink);
        this.Foreground = Foreground;
        this.Background = Background;
        this.Attributes = Attributes;
        this.Width = Width;
        this.UnderlineColor = UnderlineColor;
    }

    public string Text
    {
        get => _content is LinkedText linked ? linked.Text : (string)_content!;
        init => _content = _content is LinkedText linked ? linked with { Text = value } : value;
    }
    public string? Hyperlink
    {
        get => (_content as LinkedText)?.Hyperlink;
        init => _content = value is null ? Text : new LinkedText(Text, value);
    }
    public TerminalColor Foreground { get; init; }
    public TerminalColor Background { get; init; }
    public TerminalColor UnderlineColor { get; init; }
    public CellAttributes Attributes { get; init; }
    public CellWidth Width { get; init; }

    public void Deconstruct(out string text, out TerminalColor foreground, out TerminalColor background,
        out CellAttributes attributes, out CellWidth width, out string? hyperlink, out TerminalColor underlineColor)
    {
        text = Text;
        foreground = Foreground;
        background = Background;
        attributes = Attributes;
        width = Width;
        hyperlink = Hyperlink;
        underlineColor = UnderlineColor;
    }

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
