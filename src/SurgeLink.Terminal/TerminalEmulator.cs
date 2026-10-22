using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Default xterm compatible emulator. Bytes are parsed by
/// <see cref="AnsiParser"/> and applied to a <see cref="ScreenBuffer"/>; the
/// scrollback, alternate screen and mode bookkeeping live here. Instances are
/// not thread safe unless callers serialise access through
/// <see cref="SyncRoot"/>.
/// </summary>
public sealed class TerminalEmulator : ITerminalEmulator, IScreenHost
{
    private const int MaxWindowTitleLength = 256;
    private const int LinePoolLimit = 64;

    private readonly object _sync = new();
    private readonly ScrollbackBuffer _scrollback;
    private readonly AnsiParser _parser;
    private readonly ScreenBuffer _primaryScreen;
    private readonly ScreenBuffer _alternateScreen;
    private readonly Stack<CellLine> _linePool = new();

    private ScreenBuffer _screen;
    private SgrState _sgr;
    private SavedCursor _savedCursor;
    private SavedCursor _alternateSavedCursor;
    private TerminalModes _modes = TerminalModes.Default;
    private int _mouseModeMask;
    private long _version;
    private bool _dirty;
    private int _batchDepth;
    private int _viewportOffset;

    /// <summary>Creates an emulator with the given viewport size and scrollback cap.</summary>
    /// <param name="columns">Initial viewport width in columns.</param>
    /// <param name="rows">Initial viewport height in rows.</param>
    /// <param name="scrollbackLimit">Maximum number of lines kept above the viewport.</param>
    public TerminalEmulator(int columns = 120, int rows = 32, int scrollbackLimit = 10000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackLimit);

        ScrollbackLimit = scrollbackLimit;
        _scrollback = new ScrollbackBuffer(scrollbackLimit);
        _primaryScreen = new ScreenBuffer(columns, rows, this, isAlternate: false);
        _alternateScreen = new ScreenBuffer(columns, rows, this, isAlternate: true);
        _screen = _primaryScreen;
        _parser = new AnsiParser(this);
    }

    /// <summary>Maximum number of scrollback lines retained.</summary>
    public int ScrollbackLimit { get; }

    /// <inheritdoc />
    public int Columns => _primaryScreen.Columns;

    /// <inheritdoc />
    public int Rows => _primaryScreen.Rows;

    /// <inheritdoc />
    public long Version => _version;

    /// <inheritdoc />
    public object SyncRoot => _sync;

    /// <inheritdoc />
    public TerminalModes Modes => _modes;

    /// <inheritdoc />
    public TerminalCursor Cursor =>
        new(_screen.CursorRow, _screen.CursorColumn, _modes.CursorVisible, _modes.CursorShape);

    /// <inheritdoc />
    public int ScrollbackLength => _scrollback.Count;

    /// <inheritdoc />
    public int ViewportOffset => _viewportOffset;

    /// <inheritdoc />
    public event EventHandler? Invalidated;

    /// <inheritdoc />
    public TerminalCell GetCell(int row, int column)
    {
        if ((uint)row >= (uint)Rows || (uint)column >= (uint)Columns)
        {
            return TerminalCell.Blank;
        }

        return GetBufferCell(row, column);
    }

    private TerminalCell GetBufferCell(int row, int column)
    {
        int index = _scrollback.Count - _viewportOffset + row;
        if (index < 0)
        {
            return TerminalCell.Blank;
        }

        if (index < _scrollback.Count)
        {
            CellLine line = _scrollback[index];
            return (uint)column < (uint)line.Cells.Length ? line.Cells[column] : TerminalCell.Blank;
        }

        return _screen.GetCell(index - _scrollback.Count, column);
    }

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (data.IsEmpty)
            {
                return;
            }

            BeginBatch();
            try
            {
                // Any output from the remote side returns the view to the live tail.
                if (_viewportOffset != 0)
                {
                    _viewportOffset = 0;
                    MarkDirty();
                }

                _parser.Feed(data);
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        lock (_sync)
        {
            BeginBatch();
            try
            {
                if (columns == Columns && rows == Rows)
                {
                    return;
                }

                _primaryScreen.Resize(columns, rows);
                _alternateScreen.Resize(columns, rows);
                for (int i = 0; i < _scrollback.Count; i++)
                {
                    _scrollback[i].Resize(columns);
                }

                if (_viewportOffset > _scrollback.Count)
                {
                    _viewportOffset = _scrollback.Count;
                }
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_sync)
        {
            BeginBatch();
            try
            {
                _parser.Reset();
                _primaryScreen.Reset();
                _alternateScreen.Reset();
                _screen = _primaryScreen;
                _sgr.Reset();
                _savedCursor = default;
                _alternateSavedCursor = default;
                _mouseModeMask = 0;
                _modes = TerminalModes.Default;
                _viewportOffset = 0;
                MarkDirty();
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <inheritdoc />
    public void ScrollViewport(int deltaLines)
    {
        lock (_sync)
        {
            BeginBatch();
            try
            {
                if (_screen.IsAlternate)
                {
                    return;
                }

                int next = Math.Clamp(_viewportOffset + deltaLines, 0, _scrollback.Count);
                if (next == _viewportOffset)
                {
                    return;
                }

                _viewportOffset = next;
                MarkDirty();
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <inheritdoc />
    public void ScrollToBottom()
    {
        lock (_sync)
        {
            BeginBatch();
            try
            {
                if (_viewportOffset == 0)
                {
                    return;
                }

                _viewportOffset = 0;
                MarkDirty();
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <inheritdoc />
    public string GetSelectionText(TerminalSelection selection)
    {
        (TerminalPosition first, TerminalPosition last) = selection.Normalized();
        int startRow = Math.Clamp(first.Row, -_scrollback.Count + _viewportOffset, Rows - 1 + _viewportOffset);
        int endRow = Math.Clamp(last.Row, -_scrollback.Count + _viewportOffset, Rows - 1 + _viewportOffset);
        if (endRow < startRow)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int row = startRow; row <= endRow; row++)
        {
            if (row > startRow)
            {
                builder.Append('\n');
            }

            int fromColumn = row == first.Row ? Math.Clamp(first.Column, 0, Columns - 1) : 0;
            int toColumn = row == last.Row ? Math.Clamp(last.Column, 0, Columns - 1) : Columns - 1;
            AppendRowRange(builder, row, fromColumn, toColumn);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public string GetVisibleText()
    {
        var builder = new StringBuilder();
        for (int row = 0; row < Rows; row++)
        {
            if (row > 0)
            {
                builder.Append('\n');
            }

            AppendRowRange(builder, row, 0, Columns - 1);
        }

        return builder.ToString();
    }

    /// <summary>Prints one printable ASCII code point (fast path).</summary>
    internal void PrintAscii(byte value)
    {
        _screen.Print(
            AsciiText.Get(value),
            1,
            _sgr.Foreground,
            _sgr.Background,
            _sgr.Attributes,
            _modes.AutoWrap,
            _modes.InsertMode);
    }

    /// <summary>Prints one decoded Unicode code point.</summary>
    internal void PrintRune(Rune rune)
    {
        if (rune.Value < 0xA0)
        {
            // C1 controls decoded from UTF-8 are not printable on the grid.
            return;
        }

        int width = CharacterWidth.GetWidth(rune);
        if (width == 0)
        {
            _screen.AppendCombining(rune.ToString());
            return;
        }

        _screen.Print(
            rune.ToString(),
            width,
            _sgr.Foreground,
            _sgr.Background,
            _sgr.Attributes,
            _modes.AutoWrap,
            _modes.InsertMode);
    }

    /// <summary>Executes a C0 control character.</summary>
    internal void ExecuteControl(byte control)
    {
        switch (control)
        {
            case 0x08:
                _screen.Backspace();
                break;
            case 0x09:
                _screen.Tab();
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                _screen.LineFeed(_modes.NewLineMode);
                break;
            case 0x0D:
                _screen.CarriageReturn();
                break;
            default:
                // NUL, BEL, SO/SI and the remaining C0 codes have no visible effect.
                break;
        }
    }

    /// <summary>Handles an ESC sequence that is not CSI, OSC or a string.</summary>
    internal void HandleEsc(char final, ReadOnlySpan<char> intermediates)
    {
        if (intermediates.Length > 0)
        {
            if (intermediates[0] == '#' && final == '8')
            {
                _screen.FillScreen("E");
            }

            // Character set, keypad and UTF-8 designations are consumed and ignored.
            return;
        }

        switch (final)
        {
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                _screen.LineFeed(_modes.NewLineMode);
                break;
            case 'E':
                _screen.CarriageReturn();
                _screen.LineFeed(_modes.NewLineMode);
                break;
            case 'M':
                _screen.ReverseIndex();
                break;
            case 'H':
                _screen.SetTabStop();
                break;
            case 'c':
                Reset();
                break;
            default:
                break;
        }
    }

    /// <summary>Handles one complete CSI sequence.</summary>
    internal void HandleCsi(
        char final,
        char privateMarker,
        ReadOnlySpan<char> intermediates,
        ReadOnlySpan<int> parameters,
        ReadOnlySpan<bool> parameterIsSub)
    {
        char intermediate = intermediates.Length > 0 ? intermediates[0] : '\0';
        switch (final)
        {
            case '@':
                _screen.InsertCharacters(Parameter(parameters, 0, 1));
                break;
            case 'A':
                _screen.CursorUp(Parameter(parameters, 0, 1));
                break;
            case 'B':
                _screen.CursorDown(Parameter(parameters, 0, 1));
                break;
            case 'C':
                _screen.CursorForward(Parameter(parameters, 0, 1));
                break;
            case 'D':
                _screen.CursorBackward(Parameter(parameters, 0, 1));
                break;
            case 'E':
                _screen.CursorNextLine(Parameter(parameters, 0, 1));
                break;
            case 'F':
                _screen.CursorPreviousLine(Parameter(parameters, 0, 1));
                break;
            case 'G':
            case '\u0060':
                _screen.SetCursor(_screen.CursorRow, Parameter(parameters, 0, 1) - 1);
                break;
            case 'H':
            case 'f':
                _screen.MoveCursorAbsolute(
                    Parameter(parameters, 0, 1) - 1,
                    Parameter(parameters, 1, 1) - 1,
                    _modes.OriginMode);
                break;
            case 'I':
                _screen.ForwardTabs(Parameter(parameters, 0, 1));
                break;
            case 'J':
                EraseInDisplay(ParameterOrZero(parameters, 0));
                break;
            case 'K':
                _screen.EraseInLine(ParameterOrZero(parameters, 0));
                break;
            case 'L':
                _screen.InsertLines(Parameter(parameters, 0, 1));
                break;
            case 'M':
                _screen.DeleteLines(Parameter(parameters, 0, 1));
                break;
            case 'P':
                _screen.DeleteCharacters(Parameter(parameters, 0, 1));
                break;
            case 'S':
                _screen.ScrollUpRegion(Parameter(parameters, 0, 1));
                break;
            case 'T':
                if (parameters.Length <= 1)
                {
                    _screen.ScrollDownRegion(Parameter(parameters, 0, 1));
                }

                break;
            case 'X':
                _screen.EraseCharacters(Parameter(parameters, 0, 1));
                break;
            case 'Z':
                _screen.BackwardTabs(Parameter(parameters, 0, 1));
                break;
            case 'a':
                _screen.CursorForward(Parameter(parameters, 0, 1));
                break;
            case 'd':
                _screen.MoveCursorAbsolute(
                    Parameter(parameters, 0, 1) - 1,
                    _screen.CursorColumn,
                    _modes.OriginMode);
                break;
            case 'e':
                _screen.CursorDown(Parameter(parameters, 0, 1));
                break;
            case 'g':
                _screen.ClearTabStops(ParameterOrZero(parameters, 0));
                break;
            case 'h':
            case 'l':
                ApplyModes(final == 'h', privateMarker, parameters);
                break;
            case 'm':
                if (privateMarker == '\0')
                {
                    ApplySgr(parameters, parameterIsSub);
                }

                break;
            case 'q':
                if (intermediate == ' ')
                {
                    ApplyCursorStyle(ParameterOrZero(parameters, 0));
                }

                break;
            case 'p':
                if (intermediate == '!')
                {
                    SoftReset();
                }

                break;
            case 'r':
                if (privateMarker == '\0' && !intermediates.Contains('$'))
                {
                    SetScrollRegion(parameters);
                }

                break;
            case 's':
                if (privateMarker == '\0' && intermediates.IsEmpty)
                {
                    SaveCursor();
                }

                break;
            case 'u':
                if (privateMarker == '\0' && intermediates.IsEmpty)
                {
                    RestoreCursor();
                }

                break;
            default:
                // DSR/DA/window manipulation and unknown finals are ignored; this
                // emulator has no reverse channel to answer them on.
                break;
        }
    }

    /// <summary>Handles an OSC string (already terminated by BEL or ST).</summary>
    internal void HandleOsc(ReadOnlySpan<byte> payload)
    {
        int separator = payload.IndexOf((byte)';');
        ReadOnlySpan<byte> commandPart = separator < 0 ? payload : payload[..separator];
        if (!TryParseInt(commandPart, out int command))
        {
            return;
        }

        // OSC 0/2 set the window title. Icons (1), hyperlinks (8), clipboard (52)
        // and other operating system commands are ignored.
        if (command is not (0 or 2))
        {
            return;
        }

        ReadOnlySpan<byte> textPart = separator < 0 ? ReadOnlySpan<byte>.Empty : payload[(separator + 1)..];
        SetWindowTitle(DecodeWindowTitle(textPart));
    }

    void IScreenHost.OnScreenChanged() => MarkDirty();

    CellLine IScreenHost.OnLineScrolledOff(CellLine line)
    {
        if (_screen.IsAlternate)
        {
            return line;
        }

        CellLine? evicted = _scrollback.Add(line);
        return evicted ?? RentLine();
    }

    private static int Parameter(ReadOnlySpan<int> parameters, int index, int fallback)
    {
        if ((uint)index >= (uint)parameters.Length)
        {
            return fallback;
        }

        int value = parameters[index];
        return value > 0 ? value : fallback;
    }

    private static int ParameterOrZero(ReadOnlySpan<int> parameters, int index)
    {
        return (uint)index < (uint)parameters.Length ? Math.Max(0, parameters[index]) : 0;
    }

    private static bool TryParseInt(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        if (digits.IsEmpty)
        {
            return false;
        }

        foreach (byte digit in digits)
        {
            if (digit is < 0x30 or > 0x39)
            {
                return false;
            }

            value = Math.Min((value * 10) + (digit - 0x30), int.MaxValue / 4);
        }

        return true;
    }

    private static string DecodeWindowTitle(ReadOnlySpan<byte> data)
    {
        string raw = Encoding.UTF8.GetString(data);
        var builder = new StringBuilder(Math.Min(raw.Length, MaxWindowTitleLength));
        foreach (char character in raw)
        {
            if (character < 0x20 || character == 0x7F)
            {
                continue;
            }

            builder.Append(character);
            if (builder.Length == MaxWindowTitleLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static bool IsBlankCell(TerminalCell cell)
    {
        if (cell.Width == CellWidth.Continuation)
        {
            return true;
        }

        string text = cell.Text;
        return string.IsNullOrEmpty(text) || (text.Length == 1 && text[0] == ' ');
    }

    private void AppendRowRange(StringBuilder builder, int row, int fromColumn, int toColumnInclusive)
    {
        if (toColumnInclusive < fromColumn)
        {
            return;
        }

        int end = toColumnInclusive + 1;
        while (end > fromColumn && IsBlankCell(GetBufferCell(row, end - 1)))
        {
            end--;
        }

        for (int column = fromColumn; column < end; column++)
        {
            TerminalCell cell = GetBufferCell(row, column);
            if (cell.Width == CellWidth.Continuation)
            {
                continue;
            }

            builder.Append(cell.Text);
        }
    }

    private void MarkDirty()
    {
        _version++;
        _dirty = true;
    }

    private void BeginBatch() => _batchDepth++;

    private void EndBatch()
    {
        if (_batchDepth > 0)
        {
            _batchDepth--;
        }

        if (_batchDepth > 0 || !_dirty)
        {
            return;
        }

        _dirty = false;
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    private CellLine RentLine()
    {
        while (_linePool.Count > 0)
        {
            CellLine pooled = _linePool.Pop();
            if (pooled.Length == Columns)
            {
                pooled.Clear();
                return pooled;
            }
        }

        return new CellLine(Columns);
    }

    private void ReturnLine(CellLine line)
    {
        if (_linePool.Count >= LinePoolLimit)
        {
            return;
        }

        line.Clear();
        _linePool.Push(line);
    }

    private void ClearScrollback()
    {
        for (int i = 0; i < _scrollback.Count; i++)
        {
            ReturnLine(_scrollback[i]);
        }

        _scrollback.Clear();
        _viewportOffset = 0;
        MarkDirty();
    }

    private void SetWindowTitle(string title)
    {
        if (string.Equals(_modes.WindowTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        SetModes(_modes with { WindowTitle = title });
    }

    private void SetModes(TerminalModes modes)
    {
        if (modes == _modes)
        {
            return;
        }

        _modes = modes;
        MarkDirty();
    }

    private void ApplyModes(bool enable, char privateMarker, ReadOnlySpan<int> parameters)
    {
        if (privateMarker == '?')
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                ApplyPrivateMode(parameters[i], enable);
            }
        }
        else if (privateMarker == '\0')
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                ApplyAnsiMode(parameters[i], enable);
            }
        }
    }

    private void ApplyPrivateMode(int mode, bool enable)
    {
        switch (mode)
        {
            case 1:
                SetModes(_modes with { ApplicationCursorKeys = enable });
                break;
            case 6:
                SetModes(_modes with { OriginMode = enable });
                _screen.MoveCursorAbsolute(0, 0, enable);
                break;
            case 7:
                SetModes(_modes with { AutoWrap = enable });
                break;
            case 12:
                SetModes(_modes with { CursorBlink = enable });
                break;
            case 25:
                SetModes(_modes with { CursorVisible = enable });
                break;
            case 47:
                ToggleAlternateScreen(enable, clearOnEntry: false, clearOnExit: false, saveCursor: false);
                break;
            case 1047:
                ToggleAlternateScreen(enable, clearOnEntry: false, clearOnExit: true, saveCursor: false);
                break;
            case 1048:
                if (enable)
                {
                    SaveCursor();
                }
                else
                {
                    RestoreCursor();
                }

                break;
            case 1049:
                ToggleAlternateScreen(enable, clearOnEntry: true, clearOnExit: false, saveCursor: true);
                break;
            case 1000:
            case 1002:
            case 1003:
                SetMouseMode(mode, enable);
                break;
            case 2004:
                SetModes(_modes with { BracketedPaste = enable });
                break;
            default:
                // ?1006 (SGR mouse encoding), ?1004 focus reporting and the rest
                // are accepted without tracking.
                break;
        }
    }

    private void ApplyAnsiMode(int mode, bool enable)
    {
        switch (mode)
        {
            case 4:
                SetModes(_modes with { InsertMode = enable });
                break;
            case 20:
                SetModes(_modes with { NewLineMode = enable });
                break;
            default:
                break;
        }
    }

    private void SetMouseMode(int mode, bool enable)
    {
        int bit = mode switch
        {
            1000 => 1,
            1002 => 2,
            1003 => 4,
            _ => 0,
        };
        if (bit == 0)
        {
            return;
        }

        int mask = enable ? _mouseModeMask | bit : _mouseModeMask & ~bit;
        if (mask == _mouseModeMask)
        {
            return;
        }

        _mouseModeMask = mask;
        SetModes(_modes with { MouseTracking = mask != 0 });
    }

    private void ToggleAlternateScreen(bool enter, bool clearOnEntry, bool clearOnExit, bool saveCursor)
    {
        if (enter)
        {
            if (_screen.IsAlternate)
            {
                return;
            }

            if (saveCursor)
            {
                _alternateSavedCursor = CaptureCursor();
            }

            if (clearOnEntry)
            {
                _alternateScreen.Reset();
            }

            _alternateScreen.SetCursorRaw(_screen.CursorRow, _screen.CursorColumn, false);
            _screen = _alternateScreen;
            SetModes(_modes with { AlternateScreen = true });
            _viewportOffset = 0;
            MarkDirty();
        }
        else
        {
            if (!_screen.IsAlternate)
            {
                return;
            }

            if (clearOnExit)
            {
                _alternateScreen.Reset();
            }

            _screen = _primaryScreen;
            SetModes(_modes with { AlternateScreen = false });
            if (saveCursor && _alternateSavedCursor.Valid)
            {
                _screen.SetCursorRaw(
                    _alternateSavedCursor.Row,
                    _alternateSavedCursor.Column,
                    _alternateSavedCursor.PendingWrap);
            }

            _viewportOffset = 0;
            MarkDirty();
        }
    }

    private void SaveCursor()
    {
        _savedCursor = CaptureCursor();
        MarkDirty();
    }

    private SavedCursor CaptureCursor() => new()
    {
        Row = _screen.CursorRow,
        Column = _screen.CursorColumn,
        PendingWrap = _screen.PendingWrap,
        Sgr = _sgr,
        OriginMode = _modes.OriginMode,
        Valid = true,
    };

    private void RestoreCursor()
    {
        if (!_savedCursor.Valid)
        {
            _screen.SetCursorRaw(0, 0, false);
            _sgr.Reset();
            SetModes(_modes with { OriginMode = false });
            MarkDirty();
            return;
        }

        _screen.SetCursorRaw(_savedCursor.Row, _savedCursor.Column, _savedCursor.PendingWrap);
        _sgr = _savedCursor.Sgr;
        SetModes(_modes with { OriginMode = _savedCursor.OriginMode });
        MarkDirty();
    }

    private void SetScrollRegion(ReadOnlySpan<int> parameters)
    {
        int top = Parameter(parameters, 0, 1) - 1;
        int bottom = Parameter(parameters, 1, _screen.Rows) - 1;
        if (bottom >= _screen.Rows)
        {
            bottom = _screen.Rows - 1;
        }

        if (top < 0)
        {
            top = 0;
        }

        if (!_screen.SetScrollRegion(top, bottom))
        {
            return;
        }

        _screen.MoveCursorAbsolute(0, 0, _modes.OriginMode);
    }

    private void EraseInDisplay(int mode)
    {
        _screen.EraseInDisplay(mode);
        if (mode == 3)
        {
            ClearScrollback();
        }
    }

    private void ApplyCursorStyle(int style)
    {
        TerminalCursorShape shape = style switch
        {
            3 or 4 => TerminalCursorShape.Underline,
            5 or 6 => TerminalCursorShape.Bar,
            _ => TerminalCursorShape.Block,
        };
        bool blink = style == 0 || (style & 1) == 1;
        SetModes(_modes with { CursorShape = shape, CursorBlink = blink });
    }

    private void SoftReset()
    {
        SetModes(_modes with
        {
            ApplicationCursorKeys = false,
            InsertMode = false,
            OriginMode = false,
            AutoWrap = true,
            CursorVisible = true,
            CursorBlink = false,
            CursorShape = TerminalCursorShape.Block,
            MouseTracking = false,
            NewLineMode = false,
            BracketedPaste = false,
        });
        _mouseModeMask = 0;
        _sgr.Reset();
        _savedCursor = default;
        _screen.ResetScrollRegion();
        _screen.MoveCursorAbsolute(0, 0, false);
        MarkDirty();
    }

    private void ApplySgr(ReadOnlySpan<int> parameters, ReadOnlySpan<bool> parameterIsSub)
    {
        if (parameters.Length == 0)
        {
            _sgr.Reset();
            MarkDirty();
            return;
        }

        int index = 0;
        while (index < parameters.Length)
        {
            int start = index;
            int end = start + 1;
            while (end < parameters.Length && parameterIsSub[end])
            {
                end++;
            }

            int code = parameters[start];
            switch (code)
            {
                case 38:
                case 48:
                case 58:
                    index = ApplyExtendedColor(code, parameters, start, end);
                    break;
                default:
                    _sgr.TryApplySimple(code);
                    index = end;
                    break;
            }
        }

        MarkDirty();
    }

    private int ApplyExtendedColor(int code, ReadOnlySpan<int> parameters, int start, int end)
    {
        if (end > start + 1)
        {
            int mode = parameters[start + 1];
            if (mode == 5)
            {
                if (end > start + 2)
                {
                    SetIndexedColor(code, parameters[start + 2]);
                }

                return end;
            }

            if (mode == 2)
            {
                int values = end - (start + 2);
                if (values >= 3)
                {
                    SetRgbColor(code, parameters[end - 3], parameters[end - 2], parameters[end - 1]);
                }

                return end;
            }

            return end;
        }

        int modeIndex = start + 1;
        if ((uint)modeIndex >= (uint)parameters.Length)
        {
            return end;
        }

        int nextMode = parameters[modeIndex];
        if (nextMode == 5 && modeIndex + 1 < parameters.Length)
        {
            SetIndexedColor(code, parameters[modeIndex + 1]);
            return modeIndex + 2;
        }

        if (nextMode == 2 && modeIndex + 3 < parameters.Length)
        {
            SetRgbColor(code, parameters[modeIndex + 1], parameters[modeIndex + 2], parameters[modeIndex + 3]);
            return modeIndex + 4;
        }

        return modeIndex + 1;
    }

    private void SetIndexedColor(int code, int index)
    {
        byte paletteIndex = (byte)Math.Clamp(index, 0, 255);
        switch (code)
        {
            case 38:
                _sgr.Foreground = TerminalColor.FromIndex(paletteIndex);
                break;
            case 48:
                _sgr.Background = TerminalColor.FromIndex(paletteIndex);
                break;
            default:
                // 58 (underline colour) has no representation in TerminalCell.
                break;
        }
    }

    private void SetRgbColor(int code, int red, int green, int blue)
    {
        uint rgb = (uint)((Math.Clamp(red, 0, 255) << 16)
            | (Math.Clamp(green, 0, 255) << 8)
            | Math.Clamp(blue, 0, 255));
        switch (code)
        {
            case 38:
                _sgr.Foreground = TerminalColor.FromRgb(rgb);
                break;
            case 48:
                _sgr.Background = TerminalColor.FromRgb(rgb);
                break;
            default:
                break;
        }
    }

    /// <summary>Cursor state saved by DECSC, CSI s, DECRC, CSI u and mode 1049.</summary>
    private struct SavedCursor
    {
        public int Row;
        public int Column;
        public bool PendingWrap;
        public SgrState Sgr;
        public bool OriginMode;
        public bool Valid;
    }
}
