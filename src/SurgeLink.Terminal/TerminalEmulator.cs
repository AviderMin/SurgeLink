using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Default xterm compatible emulator. Bytes are parsed by
/// <see cref="AnsiParser"/> and applied to a <see cref="ScreenBuffer"/>; the
/// scrollback, alternate screen and mode bookkeeping live here. Instances are
/// not thread safe unless callers serialise access through
/// <see cref="SyncRoot"/>.
/// </summary>
public sealed partial class TerminalEmulator : ITerminalEmulator, IScreenHost
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
    private string? _lastGraphicText;
    private int _lastGraphicWidth;
    private bool _g0LineDrawing;
    private bool _g1LineDrawing;
    private bool _shiftOut;
    private bool _joinNextRune;
    private string? _activeHyperlink;
    private List<string>? _pendingResponses;

    /// <summary>
    /// When true, horizontal resizes reflow wrapped primary-screen and scrollback lines
    /// to the new column width instead of truncating/hiding right-edge columns.
    /// </summary>
    public bool ReflowOnResize { get; set; }

    /// <summary>
    /// Raised when the remote host sends a bounded write-only OSC 52 clipboard payload.
    /// Clipboard reads (<c>?</c>) are intentionally never answered.
    /// </summary>
    public event Action<string>? ClipboardCopyRequested;

    /// <summary>Creates an emulator with the given viewport size and scrollback cap.</summary>
    /// <param name="columns">Initial viewport width in columns.</param>
    /// <param name="rows">Initial viewport height in rows.</param>
    /// <param name="scrollbackLimit">Maximum number of lines kept above the viewport.</param>
    public TerminalEmulator(int columns = 120, int rows = 32, int scrollbackLimit = 10000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackLimit);

        _scrollback = new ScrollbackBuffer(scrollbackLimit);
        _primaryScreen = new ScreenBuffer(columns, rows, this, isAlternate: false);
        _alternateScreen = new ScreenBuffer(columns, rows, this, isAlternate: true);
        _screen = _primaryScreen;
        _parser = new AnsiParser(this);
    }

    /// <summary>Maximum number of scrollback lines retained.</summary>
    public int ScrollbackLimit => _scrollback.Limit;

    /// <summary>Copies the primary screen and history into an independent emulator for a new shell.</summary>
    public TerminalEmulator CopyHistoryForNewShell()
    {
        lock (_sync)
        {
            var copy = new TerminalEmulator(Columns, Rows, _scrollback.Limit) { ReflowOnResize = ReflowOnResize };
            copy._primaryScreen.CopyContentFrom(_primaryScreen);
            for (var row = 0; row < _scrollback.Count; row++)
                _ = copy._scrollback.Add(_scrollback[row].Copy());
            return copy;
        }
    }

    /// <summary>Changes the history cap immediately, keeping the newest lines.</summary>
    public void SetScrollbackLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (_sync)
        {
            if (limit == _scrollback.Limit) return;
            BeginBatch();
            try
            {
                _scrollback.SetLimit(limit);
                _viewportOffset = Math.Min(_viewportOffset, _scrollback.Count);
                MarkDirty();
            }
            finally { EndBatch(); }
        }
    }

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

    /// <summary>Protocol replies to send to the remote host; raised on the feeding thread.</summary>
    public event Action<string>? ResponseRequested;

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
            return _scrollback[index].GetCell(column);
        }

        return _screen.GetCell(index - _scrollback.Count, column);
    }

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<byte> data) => Feed(data, null);

    /// <summary>Records output and parses it in one transaction, then dispatches replies outside the emulator lock.</summary>
    public void Feed(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>>? recordOutput)
    {
        List<string>? responses;
        lock (_sync)
        {
            if (data.IsEmpty)
            {
                return;
            }

            BeginBatch();
            try
            {
                // Preserve the reading position while the user is inspecting history.
                // Every line added to scrollback advances the absolute viewport, so
                // OnLineScrolledOff increases the offset by one. ScrollToBottom
                // resumes live output.
                recordOutput?.Invoke(data);
                _parser.Feed(data);
            }
            finally
            {
                responses = _pendingResponses;
                _pendingResponses = null;
                EndBatch();
            }
        }
        if (responses is null) { return; }
        foreach (var response in responses) { ResponseRequested?.Invoke(response); }
    }

    private void QueueResponse(string response) => (_pendingResponses ??= []).Add(response);

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

                if (ReflowOnResize && columns != Columns)
                {
                    _primaryScreen.Reflow(columns, rows, _scrollback);
                    _alternateScreen.Resize(columns, rows);
                }
                else
                {
                    _primaryScreen.Resize(columns, rows);
                    _alternateScreen.Resize(columns, rows);
                    for (int i = 0; i < _scrollback.Count; i++)
                    {
                        _scrollback[i].Resize(columns);
                    }
                }

                if (_screen.IsAlternate && _alternateSavedCursor.Valid)
                {
                    _alternateSavedCursor = _alternateSavedCursor with
                    {
                        Row = _primaryScreen.CursorRow,
                        Column = _primaryScreen.CursorColumn,
                        PendingWrap = _primaryScreen.PendingWrap,
                    };
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
                _primaryScreen.Reset();
                _alternateScreen.Reset();
                ResetSessionState();
            }
            finally
            {
                EndBatch();
            }
        }
    }

    /// <summary>Starts a clean shell while retaining primary-screen content and scrollback.</summary>
    public void ResetForNewShell()
    {
        lock (_sync)
        {
            BeginBatch();
            try
            {
                _primaryScreen.ResetShellState();
                _alternateScreen.Reset();
                ResetSessionState();
            }
            finally { EndBatch(); }
        }
    }

    private void ResetSessionState()
    {
        ClearColorOverrides();
        _parser.Reset();
        _screen = _primaryScreen;
        _sgr.Reset();
        _savedCursor = default;
        _alternateSavedCursor = default;
        _mouseModeMask = 0;
        _modes = TerminalModes.Default;
        _viewportOffset = 0;
        _lastGraphicText = null;
        _lastGraphicWidth = 0;
        _g0LineDrawing = false;
        _g1LineDrawing = false;
        _shiftOut = false;
        _joinNextRune = false;
        _activeHyperlink = null;
        MarkDirty();
    }

    /// <summary>
    /// Clears the active screen and the scrollback without touching terminal modes,
    /// the SGR pen or the alternate-screen state. This is the "clear" action a user
    /// expects from a context menu: unlike <see cref="Reset"/> it does not drop a
    /// remote application out of the alternate screen or turn off mouse reporting.
    /// </summary>
    public void ClearDisplay()
    {
        lock (_sync)
        {
            BeginBatch();
            try
            {
                // Erase with the default cell, not whatever colour the remote left active.
                _screen.EraseCell = TerminalCell.Blank;
                _screen.EraseInDisplay(2);
                _screen.SetCursor(0, 0);
                ClearScrollback();
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
            // A soft-wrapped row is one logical line: joining the next row with a
            // newline would break a pasted command in two.
            if (row > startRow && !IsWrappedRow(row - 1))
            {
                builder.Append('\n');
            }

            int fromColumn = row == first.Row ? Math.Clamp(first.Column, 0, Columns - 1) : 0;
            int toColumn = row == last.Row ? Math.Clamp(last.Column, 0, Columns - 1) : Columns - 1;
            AppendRowRange(builder, row, fromColumn, toColumn);
        }

        return builder.ToString();
    }

    /// <summary>True when the buffer row ends without a hard line break.</summary>
    private bool IsWrappedRow(int row)
    {
        int index = _scrollback.Count - _viewportOffset + row;
        if (index < 0)
        {
            return false;
        }

        if (index < _scrollback.Count)
        {
            return _scrollback[index].Wrapped;
        }

        int screenRow = index - _scrollback.Count;
        return screenRow >= 0 && screenRow < _screen.Rows && _screen.LineAt(screenRow).Wrapped;
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

    /// <inheritdoc />
    public IReadOnlyList<TerminalSearchMatch> FindText(string query, bool matchCase = false, int maxMatches = 1000)
    {
        if (string.IsNullOrEmpty(query) || maxMatches <= 0)
        {
            return Array.Empty<TerminalSearchMatch>();
        }

        lock (_sync)
        {
            var matches = new List<TerminalSearchMatch>();
            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int scrollbackCount = _scrollback.Count;
            // Reused across lines so a full scrollback scan does not allocate two
            // collections per line on the UI thread.
            var textBuffer = new StringBuilder(256);
            var owningCellBuffer = new List<int>(256);

            // The alternate screen hides the primary scrollback; searching lines the
            // user cannot see would offer matches that scrolling can never reveal.
            if (!_screen.IsAlternate)
            {
                for (int index = 0; index < scrollbackCount && matches.Count < maxMatches; index++)
                {
                    SearchLine(_scrollback[index], index, query, comparison, maxMatches, matches, textBuffer, owningCellBuffer);
                }
            }

            for (int row = 0; row < Rows && matches.Count < maxMatches; row++)
            {
                SearchLine(_screen.GetLine(row), scrollbackCount + row, query, comparison, maxMatches, matches,
                    textBuffer, owningCellBuffer);
            }

            return matches;
        }
    }

    private static void SearchLine(
        CellLine line,
        int absoluteLine,
        string query,
        StringComparison comparison,
        int maxMatches,
        List<TerminalSearchMatch> matches,
        StringBuilder text,
        List<int> owningCell)
    {
        int length = line.Length;
        if (length == 0)
        {
            return;
        }

        // Most lines do not contain even the first character of the query. A
        // cheap cell scan avoids building the full text and column map for them,
        // which dominates a no-match search across a large scrollback.
        char firstCharacter = query[0];
        bool matchCase = comparison == StringComparison.Ordinal;
        bool mayContain = false;
        for (int column = 0; column < length && !mayContain; column++)
        {
            TerminalCell cell = line.GetCell(column);
            if (cell.Width == CellWidth.Continuation)
            {
                continue;
            }

            string value = (cell.Attributes & CellAttributes.Hidden) != 0 ? " " : cell.Text ?? " ";
            for (int index = 0; index < value.Length; index++)
            {
                if (matchCase
                    ? value[index] == firstCharacter
                    : char.ToUpperInvariant(value[index]) == char.ToUpperInvariant(firstCharacter))
                {
                    mayContain = true;
                    break;
                }
            }
        }

        if (!mayContain)
        {
            return;
        }

        text.Clear();
        owningCell.Clear();
        for (int column = 0; column < length; column++)
        {
            TerminalCell cell = line.GetCell(column);
            if (cell.Width == CellWidth.Continuation)
            {
                continue;
            }

            string value = (cell.Attributes & CellAttributes.Hidden) != 0 ? " " : cell.Text ?? " ";
            if (value.Length == 0)
            {
                value = " ";
            }

            text.Append(value);
            for (int i = 0; i < value.Length; i++)
            {
                owningCell.Add(column);
            }
        }

        string haystack = text.ToString();
        int searchFrom = 0;
        while (searchFrom <= haystack.Length - query.Length && matches.Count < maxMatches)
        {
            int offset = haystack.AsSpan(searchFrom).IndexOf(query.AsSpan(), comparison);
            if (offset < 0)
            {
                return;
            }

            int characterIndex = searchFrom + offset;
            int firstCell = owningCell[characterIndex];
            int lastCell = owningCell[characterIndex + query.Length - 1];
            int endColumn = lastCell;
            if (line.GetCell(lastCell).Width == CellWidth.Wide && endColumn + 1 < length)
            {
                // A wide glyph claims its continuation column; highlight both halves.
                endColumn++;
            }

            matches.Add(new TerminalSearchMatch(absoluteLine, firstCell, endColumn - firstCell + 1));
            searchFrom = characterIndex + 1;
        }
    }

    /// <summary>Prints one printable ASCII code point (fast path).</summary>
    internal void PrintAscii(byte value)
    {
        _joinNextRune = false;
        UpdateEraseCell();
        string text = (_shiftOut ? _g1LineDrawing : _g0LineDrawing) && MapDecSpecialGraphics(value) is { } mapped
            ? mapped
            : AsciiText.Get(value);
        _lastGraphicText = text;
        _lastGraphicWidth = 1;
        _screen.Print(
            text,
            1,
            _sgr.Foreground,
            _sgr.Background,
            _sgr.Attributes,
            _modes.AutoWrap,
            _modes.InsertMode,
            _activeHyperlink,
            _sgr.UnderlineColor);
    }

    private static string? MapDecSpecialGraphics(byte value) => value switch
    {
        0x60 => "◆",
        0x61 => "▒",
        0x66 => "°",
        0x67 => "±",
        0x6A => "┘",
        0x6B => "┐",
        0x6C => "┌",
        0x6D => "└",
        0x6E => "┼",
        0x6F => "⎺",
        0x70 => "⎻",
        0x71 => "─",
        0x72 => "⎼",
        0x73 => "⎽",
        0x74 => "├",
        0x75 => "┤",
        0x76 => "┴",
        0x77 => "┬",
        0x78 => "│",
        0x79 => "≤",
        0x7A => "≥",
        0x7B => "π",
        0x7C => "≠",
        0x7D => "£",
        0x7E => "·",
        _ => null,
    };

    /// <summary>Prints one decoded Unicode code point.</summary>
    internal void PrintRune(Rune rune)
    {
        UpdateEraseCell();
        if (rune.Value < 0xA0)
        {
            _joinNextRune = false;
            // C1 controls decoded from UTF-8 are not printable on the grid.
            return;
        }

        if (rune.Value == 0x200D)
        {
            _joinNextRune = _screen.AppendCombining(rune.ToString());
            return;
        }

        if (_joinNextRune)
        {
            _joinNextRune = false;
            if (_screen.AppendCombining(rune.ToString(), requireSameGrapheme: true)) return;
        }

        if (rune.Value is >= 0x1F3FB and <= 0x1F3FF
            || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.SpacingCombiningMark)
        {
            if (_screen.AppendCombining(rune.ToString(), requireSameGrapheme: true)) return;
        }

        int width = CharacterWidth.GetWidth(rune);
        if (width == 0)
        {
            _screen.AppendCombining(rune.ToString());
            return;
        }

        string text = rune.ToString();
        _lastGraphicText = text;
        _lastGraphicWidth = width;
        _screen.Print(
            text,
            width,
            _sgr.Foreground,
            _sgr.Background,
            _sgr.Attributes,
            _modes.AutoWrap,
            _modes.InsertMode,
            _activeHyperlink,
            _sgr.UnderlineColor);
    }

    /// <summary>Executes a C0 control character.</summary>
    internal void ExecuteControl(byte control)
    {
        _joinNextRune = false;
        UpdateEraseCell();
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
            case 0x0E:
                _shiftOut = true;
                break;
            case 0x0F:
                _shiftOut = false;
                break;
            default:
                // NUL, BEL and the remaining C0 codes have no visible effect.
                break;
        }
    }

    /// <summary>Handles an ESC sequence that is not CSI, OSC or a string.</summary>
    internal void HandleEsc(char final, ReadOnlySpan<char> intermediates)
    {
        _joinNextRune = false;
        UpdateEraseCell();
        if (intermediates.Length > 0)
        {
            if (intermediates[0] == '#' && final == '8')
            {
                _screen.FillScreen("E");
            }
            else if (intermediates.Length == 1 && intermediates[0] == '(')
            {
                _g0LineDrawing = final == '0';
                MarkDirty();
            }
            else if (intermediates.Length == 1 && intermediates[0] == ')')
            {
                _g1LineDrawing = final == '0';
                MarkDirty();
            }

            // Remaining character set, keypad and UTF-8 designations are consumed and ignored.
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
            case 'Z':
                QueueResponse("\x1b[?1;0c");
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
        _joinNextRune = false;
        UpdateEraseCell();
        char intermediate = intermediates.Length > 0 ? intermediates[0] : '\0';
        bool standard = privateMarker == '\0' && intermediates.IsEmpty;
        switch (final)
        {
            case '@' when standard:
                _screen.InsertCharacters(Parameter(parameters, 0, 1));
                break;
            case 'A' when standard:
                _screen.CursorUp(Parameter(parameters, 0, 1));
                break;
            case 'B' when standard:
                _screen.CursorDown(Parameter(parameters, 0, 1));
                break;
            case 'C' when standard:
                _screen.CursorForward(Parameter(parameters, 0, 1));
                break;
            case 'D' when standard:
                _screen.CursorBackward(Parameter(parameters, 0, 1));
                break;
            case 'E' when standard:
                _screen.CursorNextLine(Parameter(parameters, 0, 1));
                break;
            case 'F' when standard:
                _screen.CursorPreviousLine(Parameter(parameters, 0, 1));
                break;
            case 'G' when standard:
            case '\u0060' when standard:
                _screen.SetCursor(_screen.CursorRow, Parameter(parameters, 0, 1) - 1);
                break;
            case 'H' when standard:
            case 'f' when standard:
                _screen.MoveCursorAbsolute(
                    Parameter(parameters, 0, 1) - 1,
                    Parameter(parameters, 1, 1) - 1,
                    _modes.OriginMode);
                break;
            case 'I' when standard:
                _screen.ForwardTabs(Parameter(parameters, 0, 1));
                break;
            case 'J' when standard:
                EraseInDisplay(ParameterOrZero(parameters, 0));
                break;
            case 'K' when standard:
                _screen.EraseInLine(ParameterOrZero(parameters, 0));
                break;
            case 'L' when standard:
                _screen.InsertLines(Parameter(parameters, 0, 1));
                break;
            case 'M' when standard:
                _screen.DeleteLines(Parameter(parameters, 0, 1));
                break;
            case 'P' when standard:
                _screen.DeleteCharacters(Parameter(parameters, 0, 1));
                break;
            case 'S' when standard:
                _screen.ScrollUpRegion(Parameter(parameters, 0, 1));
                break;
            case 'T' when standard:
                if (parameters.Length <= 1)
                {
                    _screen.ScrollDownRegion(Parameter(parameters, 0, 1));
                }

                break;
            case 'X' when standard:
                _screen.EraseCharacters(Parameter(parameters, 0, 1));
                break;
            case 'Z' when standard:
                _screen.BackwardTabs(Parameter(parameters, 0, 1));
                break;
            case 'a' when standard:
                _screen.CursorForward(Parameter(parameters, 0, 1));
                break;
            case 'b' when standard:
                if (_lastGraphicText is { } repeatText && _lastGraphicWidth > 0)
                {
                    int repeatCount = Math.Clamp(Parameter(parameters, 0, 1), 1, 9999);
                    for (int i = 0; i < repeatCount; i++)
                    {
                        _screen.Print(
                            repeatText,
                            _lastGraphicWidth,
                            _sgr.Foreground,
                            _sgr.Background,
                            _sgr.Attributes,
                            _modes.AutoWrap,
                            _modes.InsertMode,
                            _activeHyperlink,
                            _sgr.UnderlineColor);
                    }
                }
                break;
            case 'd' when standard:
                _screen.MoveCursorAbsolute(
                    Parameter(parameters, 0, 1) - 1,
                    _screen.CursorColumn,
                    _modes.OriginMode);
                break;
            case 'e' when standard:
                _screen.CursorDown(Parameter(parameters, 0, 1));
                break;
            case 'g' when standard:
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
            case 'n' when intermediates.IsEmpty:
                if (privateMarker == '\0' && ParameterOrZero(parameters, 0) == 5)
                {
                    QueueResponse("\x1b[0n");
                }
                else if ((privateMarker == '\0' || privateMarker == '?') && ParameterOrZero(parameters, 0) == 6)
                {
                    var row = _screen.CursorRow - (_modes.OriginMode ? _screen.ScrollTop : 0) + 1;
                    var prefix = privateMarker == '?' ? "?" : string.Empty;
                    QueueResponse(FormattableString.Invariant($"\x1b[{prefix}{row};{_screen.CursorColumn + 1}R"));
                }
                break;
            case 'c' when intermediates.IsEmpty && ParameterOrZero(parameters, 0) == 0:
                if (privateMarker == '\0') { QueueResponse("\x1b[?1;0c"); }
                else if (privateMarker == '>') { QueueResponse("\x1b[>0;1;0c"); }
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
                // Unsupported queries and window manipulation are ignored.
                break;
        }
    }

    /// <summary>Returns the OSC 8 hyperlink URI attached to the visible cell, or null.</summary>
    public string? GetHyperlinkAt(int row, int column)
    {
        lock (_sync)
        {
            return GetCell(row, column).Hyperlink;
        }
    }

    /// <summary>Handles an OSC string (already terminated by BEL or ST).</summary>
    internal void HandleOsc(ReadOnlySpan<byte> payload, bool complete, bool bellTerminated)
    {
        int separator = payload.IndexOf((byte)';');
        ReadOnlySpan<byte> commandPart = separator < 0 ? payload : payload[..separator];
        if (!TryParseInt(commandPart, out int command))
        {
            return;
        }

        ReadOnlySpan<byte> rest = separator < 0 ? ReadOnlySpan<byte>.Empty : payload[(separator + 1)..];
        switch (command)
        {
            case 0:
            case 2:
                SetWindowTitle(DecodeWindowTitle(rest));
                break;
            case 8:
                HandleOsc8Hyperlink(rest);
                break;
            case 52 when complete:
                HandleOsc52Clipboard(rest);
                break;
            case 4 or 10 or 11 or 12 or 104 or 110 or 111 or 112 when complete:
                HandleOscColor(command, rest, bellTerminated);
                break;
        }
    }

    private void HandleOsc8Hyperlink(ReadOnlySpan<byte> rest)
    {
        int uriSeparator = rest.IndexOf((byte)';');
        if (uriSeparator < 0)
        {
            _activeHyperlink = null;
            return;
        }

        ReadOnlySpan<byte> uriBytes = rest[(uriSeparator + 1)..];
        if (uriBytes.IsEmpty)
        {
            _activeHyperlink = null;
            return;
        }

        if (uriBytes.Length > 2048) return;
        string uri = Encoding.UTF8.GetString(uriBytes).Trim();
        if (uri.Length == 0)
        {
            _activeHyperlink = null;
            return;
        }

        if (uri.Any(char.IsControl)) return;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            _activeHyperlink = parsed.AbsoluteUri;
        }
    }

    private void HandleOsc52Clipboard(ReadOnlySpan<byte> rest)
    {
        int dataSeparator = rest.IndexOf((byte)';');
        if (dataSeparator < 0) return;
        ReadOnlySpan<byte> dataBytes = rest[(dataSeparator + 1)..];
        if (dataBytes.IsEmpty || (dataBytes.Length == 1 && dataBytes[0] == (byte)'?'))
        {
            // Never expose local clipboard contents to a remote host.
            return;
        }
        string base64 = Encoding.ASCII.GetString(dataBytes).Trim();
        if (base64.Length == 0) return;
        try
        {
            byte[] decoded = Convert.FromBase64String(base64);
            ClipboardCopyRequested?.Invoke(Encoding.UTF8.GetString(decoded));
        }
        catch (FormatException)
        {
        }
    }

    void IScreenHost.OnScreenChanged() => MarkDirty();

    CellLine IScreenHost.OnLineScrolledOff(CellLine line)
    {
        // Only _primaryScreen invokes OnLineScrolledOff; still record lines pushed off
        // during a primary-screen resize even when the alternate screen is active.
        CellLine? evicted = _scrollback.Add(line);
        if (!_screen.IsAlternate && _viewportOffset > 0)
        {
            // One more line now sits above the live screen, so the absolute
            // position of the viewport start advances by one. Raising the offset
            // by the same amount keeps the lines the user is reading on screen.
            // This also holds when the ring buffer evicts its oldest line: the
            // count stays the same but every retained index shifts by one.
            _viewportOffset = Math.Min(_viewportOffset + 1, _scrollback.Count);
        }
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

    private void UpdateEraseCell() => _screen.EraseCell = new TerminalCell(" ",
        _sgr.Foreground, _sgr.Background, CellAttributes.None, CellWidth.Narrow);

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
            case 1006:
                SetModes(_modes with { SgrMouseEncoding = enable });
                break;
            default:
                // ?1004 focus reporting and the rest
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
        SetModes(_modes with
        {
            MouseTracking = mask != 0,
            MouseTrackingMode = (mask & 4) != 0 ? TerminalMouseTrackingMode.Any
                : (mask & 2) != 0 ? TerminalMouseTrackingMode.Drag
                : mask != 0 ? TerminalMouseTrackingMode.Buttons : TerminalMouseTrackingMode.None,
        });
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
                // xterm's alternate screen keeps a full cursor state: leaving an
                // application must not leak its colours or origin mode into the
                // primary shell (for example after quitting vim or less).
                _sgr = _alternateSavedCursor.Sgr;
                SetModes(_modes with { OriginMode = _alternateSavedCursor.OriginMode });
            }
            _alternateSavedCursor = default;

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
            MouseTrackingMode = TerminalMouseTrackingMode.None,
            SgrMouseEncoding = false,
            NewLineMode = false,
            BracketedPaste = false,
        });
        _mouseModeMask = 0;
        _sgr.Reset();
        _savedCursor = default;
        _lastGraphicText = null;
        _lastGraphicWidth = 0;
        _g0LineDrawing = false;
        _g1LineDrawing = false;
        _shiftOut = false;
        _joinNextRune = false;
        _activeHyperlink = null;
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
                case 4 when end > start + 1:
                    // ITU T.416 / Kitty underline style subparameter: 4:0 clears underline, 4:1..5 enables it.
                    int style = parameters[start + 1];
                    _sgr.TryApplySimple(style == 0 ? 24 : 4);
                    index = end;
                    break;
                case 38:
                case 48:
                case 58:
                    index = ApplyExtendedColor(code, parameters, parameterIsSub, start, end);
                    break;
                default:
                    _sgr.TryApplySimple(code);
                    index = end;
                    break;
            }
        }

        MarkDirty();
    }

    private int ApplyExtendedColor(int code, ReadOnlySpan<int> parameters, ReadOnlySpan<bool> parameterIsSub, int start, int end)
    {
        int modeIndex = start + 1;
        if (modeIndex >= parameters.Length) { return end; }

        // xterm also accepts a semicolon before the first colon-separated subparameter.
        if (end == modeIndex && modeIndex + 1 < parameters.Length
            && parameterIsSub[modeIndex + 1] && parameters[modeIndex] is 2 or 5)
        {
            end = modeIndex + 2;
            while (end < parameters.Length && parameterIsSub[end]) { end++; }
        }

        if (end > modeIndex)
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
                int values = end - (modeIndex + 1);
                if (values >= 3)
                {
                    int first = modeIndex + (values == 3 ? 1 : 2);
                    SetRgbColor(code, parameters[first], parameters[first + 1], parameters[first + 2]);
                }

                return end;
            }

            return end;
        }

        int nextMode = parameters[modeIndex];
        if (modeIndex + 1 < parameters.Length && parameterIsSub[modeIndex + 1]) { return modeIndex; }
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
        SetColor(code, TerminalColor.FromIndex((byte)Math.Clamp(index, 0, 255)) with { IsExplicitIndex = true });
    }

    private void SetRgbColor(int code, int red, int green, int blue)
    {
        uint rgb = (uint)((Math.Clamp(red, 0, 255) << 16)
            | (Math.Clamp(green, 0, 255) << 8)
            | Math.Clamp(blue, 0, 255));
        SetColor(code, TerminalColor.FromRgb(rgb));
    }

    private void SetColor(int code, TerminalColor color)
    {
        switch (code)
        {
            case 38:
                _sgr.Foreground = color;
                break;
            case 48:
                _sgr.Background = color;
                break;
            case 58:
                _sgr.UnderlineColor = color;
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
