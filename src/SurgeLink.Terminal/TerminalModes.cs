namespace SurgeLink.Terminal;

/// <summary>Xterm mouse reporting scope.</summary>
public enum TerminalMouseTrackingMode { None, Buttons, Drag, Any }

/// <summary>Cursor shapes requested with DECSCUSR.</summary>
public enum TerminalCursorShape
{
    Block,
    Underline,
    Bar,
}

/// <summary>Cursor position and appearance.</summary>
public readonly record struct TerminalCursor(int Row, int Column, bool Visible, TerminalCursorShape Shape)
{
    /// <summary>Cursor at the origin, visible, block shape.</summary>
    public static TerminalCursor Default { get; } = new(0, 0, true, TerminalCursorShape.Block);
}

/// <summary>Terminal wide state tracked by the emulator.</summary>
public sealed record TerminalModes
{
    public static TerminalModes Default { get; } = new();

    /// <summary>DECCKM: arrows send SS3 sequences.</summary>
    public bool ApplicationCursorKeys { get; init; }

    /// <summary>Alternate screen buffer is active (vim, less, htop).</summary>
    public bool AlternateScreen { get; init; }

    /// <summary>Bracketed paste is enabled by the remote application.</summary>
    public bool BracketedPaste { get; init; }

    /// <summary>IRM: typing shifts existing cells right.</summary>
    public bool InsertMode { get; init; }

    /// <summary>LNM: a line feed also returns the cursor to column 0 (ANSI mode 20).</summary>
    public bool NewLineMode { get; init; }

    /// <summary>DECOM: cursor addressing is relative to the scroll region.</summary>
    public bool OriginMode { get; init; }

    /// <summary>DECAWM: wrap at the right margin.</summary>
    public bool AutoWrap { get; init; } = true;

    /// <summary>DECTCEM: cursor visible.</summary>
    public bool CursorVisible { get; init; } = true;

    /// <summary>Whether the terminal reports mouse events to the application.</summary>
    public bool MouseTracking { get; init; }

    public TerminalMouseTrackingMode MouseTrackingMode { get; init; }

    public bool SgrMouseEncoding { get; init; }

    /// <summary>Current cursor shape.</summary>
    public TerminalCursorShape CursorShape { get; init; } = TerminalCursorShape.Block;

    /// <summary>DECSCUSR blink attribute, toggled by mode ?12.</summary>
    public bool CursorBlink { get; init; }

    /// <summary>Window title reported by the remote application (OSC 0/2), when any.</summary>
    public string? WindowTitle { get; init; }
}
