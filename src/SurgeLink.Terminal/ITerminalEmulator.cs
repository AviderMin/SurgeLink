namespace SurgeLink.Terminal;

/// <summary>
/// A VT100/xterm compatible screen buffer. Bytes go in, a cell grid comes out.
/// Implementations are not thread safe: callers must serialise access with
/// <see cref="SyncRoot"/> (the UI renderer does this) or own the emulator from
/// a single thread.
/// </summary>
public interface ITerminalEmulator
{
    /// <summary>Viewport width in columns.</summary>
    int Columns { get; }

    /// <summary>Viewport height in rows.</summary>
    int Rows { get; }

    /// <summary>Incremented on every mutation; renderers use it to skip repaints.</summary>
    long Version { get; }

    /// <summary>Object used to serialise reads against writes.</summary>
    object SyncRoot { get; }

    /// <summary>Current wide terminal state.</summary>
    TerminalModes Modes { get; }

    /// <summary>Cursor position and appearance.</summary>
    TerminalCursor Cursor { get; }

    /// <summary>Lines currently preserved above the viewport.</summary>
    int ScrollbackLength { get; }

    /// <summary>How many lines the view is scrolled back (0 = live view).</summary>
    int ViewportOffset { get; }

    /// <summary>Raised after a mutation, on the thread that fed the data.</summary>
    event EventHandler? Invalidated;

    /// <summary>Reads one viewport cell. Out of range coordinates return <see cref="TerminalCell.Blank"/>.</summary>
    TerminalCell GetCell(int row, int column);

    /// <summary>Feeds raw bytes received from the remote shell.</summary>
    void Feed(ReadOnlySpan<byte> data);

    /// <summary>Resizes the viewport, reflowing is not required (truncate/pad is acceptable).</summary>
    void Resize(int columns, int rows);

    /// <summary>Resets the screen, attributes and modes, keeping scrollback.</summary>
    void Reset();

    /// <summary>Scrolls the viewport by <paramref name="deltaLines"/> (positive scrolls back).</summary>
    void ScrollViewport(int deltaLines);

    /// <summary>Returns to the live view.</summary>
    void ScrollToBottom();

    /// <summary>Extracts the text currently covered by a selection.</summary>
    string GetSelectionText(TerminalSelection selection);

    /// <summary>Visible text with trailing blanks trimmed; used by accessibility and tests.</summary>
    string GetVisibleText();
}
