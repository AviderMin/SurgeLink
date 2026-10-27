namespace SurgeLink.Terminal;

/// <summary>Services a <see cref="ScreenBuffer"/> needs from its owner.</summary>
internal interface IScreenHost
{
    /// <summary>Called after every mutation so the emulator can bump its version.</summary>
    void OnScreenChanged();

    /// <summary>
    /// Called when a line scrolls off the top of the screen. Returns the line
    /// the screen should use for the vacated bottom row.
    /// </summary>
    CellLine OnLineScrolledOff(CellLine line);
}

/// <summary>
/// Cell grid with cursor, scrolling margins and tab stops for one screen
/// (primary or alternate). All coordinates are zero based.
/// </summary>
internal sealed class ScreenBuffer
{
    internal const int MaximumCellTextLength = 64;
    public TerminalCell EraseCell { private get; set; } = TerminalCell.Blank;
    private readonly IScreenHost _host;
    private readonly bool _isAlternate;

    private CellLine[] _lines;
    private bool[] _tabStops;
    private int _columns;
    private int _rows;
    private int _cursorRow;
    private int _cursorColumn;
    private bool _pendingWrap;
    private int _scrollTop;
    private int _scrollBottom;

    public ScreenBuffer(int columns, int rows, IScreenHost host, bool isAlternate)
    {
        _host = host;
        _isAlternate = isAlternate;
        _columns = columns;
        _rows = rows;
        _lines = CreateLines(columns, rows);
        _tabStops = CreateTabStops(columns);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
    }

    /// <summary>Viewport width in columns.</summary>
    public int Columns => _columns;

    /// <summary>Viewport height in rows.</summary>
    public int Rows => _rows;

    /// <summary>True for the alternate screen buffer.</summary>
    public bool IsAlternate => _isAlternate;

    /// <summary>Cursor row, zero based.</summary>
    public int CursorRow => _cursorRow;

    /// <summary>Cursor column, zero based.</summary>
    public int CursorColumn => _cursorColumn;

    /// <summary>True when a glyph was written into the last column and the next glyph must wrap first.</summary>
    public bool PendingWrap => _pendingWrap;

    /// <summary>First row of the scrolling region, zero based and inclusive.</summary>
    public int ScrollTop => _scrollTop;

    /// <summary>Last row of the scrolling region, zero based and inclusive.</summary>
    public int ScrollBottom => _scrollBottom;

    /// <summary>Reads a cell; out of range coordinates return <see cref="TerminalCell.Blank"/>.</summary>
    public TerminalCell GetCell(int row, int column)
    {
        if ((uint)row >= (uint)_rows || (uint)column >= (uint)_columns)
        {
            return TerminalCell.Blank;
        }

        return _lines[row].Cells[column];
    }

    /// <summary>Moves the cursor inside the screen, cancelling a pending wrap.</summary>
    public void SetCursor(int row, int column)
    {
        row = Math.Clamp(row, 0, _rows - 1);
        column = Math.Clamp(column, 0, _columns - 1);
        if (row == _cursorRow && column == _cursorColumn && !_pendingWrap)
        {
            return;
        }

        _cursorRow = row;
        _cursorColumn = column;
        _pendingWrap = false;
        _host.OnScreenChanged();
    }

    /// <summary>Restores an exact cursor including the deferred wrap flag.</summary>
    public void SetCursorRaw(int row, int column, bool pendingWrap)
    {
        _cursorRow = Math.Clamp(row, 0, _rows - 1);
        _cursorColumn = Math.Clamp(column, 0, _columns - 1);
        _pendingWrap = pendingWrap;
        _host.OnScreenChanged();
    }

    /// <summary>Moves the cursor to an absolute position honouring origin mode.</summary>
    public void MoveCursorAbsolute(int row, int column, bool originMode)
    {
        int minRow = originMode ? _scrollTop : 0;
        int maxRow = originMode ? _scrollBottom : _rows - 1;
        if (originMode)
        {
            row += _scrollTop;
        }

        SetCursor(Math.Clamp(row, minRow, maxRow), column);
    }

    /// <summary>Carriage return: column 0 on the current row.</summary>
    public void CarriageReturn() => SetCursor(_cursorRow, 0);

    /// <summary>Line feed; scrolls the region when the cursor is on its bottom row.</summary>
    public void LineFeed(bool newLineMode)
    {
        if (newLineMode)
        {
            _cursorColumn = 0;
        }

        if (_cursorRow == _scrollBottom)
        {
            ScrollUpInternal(1);
        }
        else if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }

        _pendingWrap = false;
        _host.OnScreenChanged();
    }

    /// <summary>Reverse index: cursor up, scrolling the region down at its top row.</summary>
    public void ReverseIndex()
    {
        if (_cursorRow == _scrollTop)
        {
            ScrollDownInternal(1);
        }
        else if (_cursorRow > 0)
        {
            _cursorRow--;
        }

        _pendingWrap = false;
        _host.OnScreenChanged();
    }

    /// <summary>Backspace; also cancels a deferred wrap.</summary>
    public void Backspace() => SetCursor(_cursorRow, _cursorColumn - 1);

    /// <summary>Moves up, stopping at the top margin when the cursor is inside the region.</summary>
    public void CursorUp(int count)
    {
        count = Math.Max(1, count);
        int limit = _cursorRow >= _scrollTop ? _scrollTop : 0;
        SetCursor(Math.Max(limit, _cursorRow - count), _cursorColumn);
    }

    /// <summary>Moves down, stopping at the bottom margin when the cursor is inside the region.</summary>
    public void CursorDown(int count)
    {
        count = Math.Max(1, count);
        int limit = _cursorRow <= _scrollBottom ? _scrollBottom : _rows - 1;
        SetCursor(Math.Min(limit, _cursorRow + count), _cursorColumn);
    }

    /// <summary>Moves right, clamped to the last column.</summary>
    public void CursorForward(int count) => SetCursor(_cursorRow, _cursorColumn + Math.Max(1, count));

    /// <summary>Moves left, clamped to the first column.</summary>
    public void CursorBackward(int count) => SetCursor(_cursorRow, _cursorColumn - Math.Max(1, count));

    /// <summary>Cursor next line (CNL).</summary>
    public void CursorNextLine(int count)
    {
        CursorDown(count);
        SetCursor(_cursorRow, 0);
    }

    /// <summary>Cursor previous line (CPL).</summary>
    public void CursorPreviousLine(int count)
    {
        CursorUp(count);
        SetCursor(_cursorRow, 0);
    }

    /// <summary>Horizontal tab to the next tab stop.</summary>
    public void Tab() => ForwardTabs(1);

    /// <summary>Moves forward over tab stops.</summary>
    public void ForwardTabs(int count)
    {
        int column = _cursorColumn;
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            int next = column + 1;
            while (next < _columns - 1 && (!_tabStops[next] || _lines[_cursorRow].Cells[next].Width == CellWidth.Continuation))
            {
                next++;
            }

            column = Math.Min(next, _columns - 1);
        }

        SetCursor(_cursorRow, column);
    }

    /// <summary>Moves backward over tab stops.</summary>
    public void BackwardTabs(int count)
    {
        int column = _cursorColumn;
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            int previous = column - 1;
            while (previous > 0 && !_tabStops[previous])
            {
                previous--;
            }

            column = Math.Max(previous, 0);
        }

        SetCursor(_cursorRow, column);
    }

    /// <summary>Sets a horizontal tab stop at the cursor column (HTS).</summary>
    public void SetTabStop()
    {
        if (_tabStops[_cursorColumn])
        {
            return;
        }

        _tabStops[_cursorColumn] = true;
        _host.OnScreenChanged();
    }

    /// <summary>Clears tab stops: mode 0 is the current column, mode 3 clears all.</summary>
    public void ClearTabStops(int mode)
    {
        if (mode == 0)
        {
            if (!_tabStops[_cursorColumn])
            {
                return;
            }

            _tabStops[_cursorColumn] = false;
        }
        else if (mode == 3)
        {
            Array.Fill(_tabStops, false);
        }
        else
        {
            return;
        }

        _host.OnScreenChanged();
    }

    /// <summary>
    /// Writes one glyph. <paramref name="width"/> is 1 for ordinary glyphs and
    /// 2 for East Asian wide or emoji glyphs which also claim a continuation cell.
    /// </summary>
    public void Print(
        string text,
        int width,
        TerminalColor foreground,
        TerminalColor background,
        CellAttributes attributes,
        bool autoWrap,
        bool insertMode)
    {
        if (_columns < 1)
        {
            return;
        }

        if (width > _columns)
        {
            width = 1;
        }

        if (width < 1)
        {
            width = 1;
        }

        if (_pendingWrap)
        {
            if (autoWrap)
            {
                _pendingWrap = false;
                _cursorColumn = 0;
                LineFeedInternal();
            }
            else
            {
                _pendingWrap = false;
            }
        }

        if (width == 2 && _cursorColumn >= _columns - 1)
        {
            if (autoWrap)
            {
                _pendingWrap = false;
                _cursorColumn = 0;
                LineFeedInternal();
            }
            else
            {
                TerminalCell[] lastCells = _lines[_cursorRow].Cells;
                ClearWidePair(lastCells, _columns - 1);
                lastCells[_columns - 1] = EraseCell;
                _pendingWrap = true;
                _host.OnScreenChanged();
                return;
            }
        }

        if (insertMode)
        {
            InsertCellsInternal(_cursorRow, _cursorColumn, width);
        }

        TerminalCell[] cells = _lines[_cursorRow].Cells;
        ClearWidePair(cells, _cursorColumn);
        if (width == 2)
        {
            ClearWidePair(cells, _cursorColumn + 1);
        }

        cells[_cursorColumn] = new TerminalCell(
            text,
            foreground,
            background,
            attributes,
            width == 2 ? CellWidth.Wide : CellWidth.Narrow);
        if (width == 2)
        {
            cells[_cursorColumn + 1] = new TerminalCell(
                string.Empty,
                foreground,
                background,
                attributes,
                CellWidth.Continuation);
        }

        _cursorColumn += width;
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = _columns - 1;
            _pendingWrap = true;
        }

        _host.OnScreenChanged();
    }

    /// <summary>Appends a zero width combining mark to the preceding cell.</summary>
    public void AppendCombining(string mark)
    {
        int column = _cursorColumn;
        if (!_pendingWrap)
        {
            if (column == 0)
            {
                return;
            }

            column--;
        }

        TerminalCell[] cells = _lines[_cursorRow].Cells;
        if (cells[column].Width == CellWidth.Continuation && column > 0)
        {
            column--;
        }

        ref TerminalCell cell = ref cells[column];
        string text = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;
        if (text.Length > MaximumCellTextLength - mark.Length) return;
        cell = cell.WithText(string.Concat(text, mark));
        _host.OnScreenChanged();
    }

    /// <summary>Erase in display: 0 to end, 1 to start, 2/3 whole screen.</summary>
    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 1:
                for (int row = 0; row < _cursorRow; row++)
                {
                    ClearRow(row);
                }

                ClearCells(_cursorRow, 0, _cursorColumn + 1);
                break;
            case 2:
            case 3:
                for (int row = 0; row < _rows; row++)
                {
                    ClearRow(row);
                }

                break;
            default:
                ClearCells(_cursorRow, _cursorColumn, _columns);
                for (int row = _cursorRow + 1; row < _rows; row++)
                {
                    ClearRow(row);
                }

                break;
        }

        _host.OnScreenChanged();
    }

    /// <summary>Erase in line: 0 to end, 1 to start, 2 whole line.</summary>
    public void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 1:
                ClearCells(_cursorRow, 0, _cursorColumn + 1);
                break;
            case 2:
                ClearRow(_cursorRow);
                break;
            default:
                ClearCells(_cursorRow, _cursorColumn, _columns);
                break;
        }

        _host.OnScreenChanged();
    }

    /// <summary>Blanks <paramref name="count"/> cells starting at the cursor.</summary>
    public void EraseCharacters(int count)
    {
        count = Math.Max(1, count);
        ClearCells(_cursorRow, _cursorColumn, _cursorColumn + count);
        _host.OnScreenChanged();
    }

    /// <summary>Inserts blank cells at the cursor, shifting the rest right.</summary>
    public void InsertCharacters(int count)
    {
        count = Math.Max(1, count);
        InsertCellsInternal(_cursorRow, _cursorColumn, Math.Min(count, _columns));
        _host.OnScreenChanged();
    }

    /// <summary>Deletes cells at the cursor, shifting the rest left.</summary>
    public void DeleteCharacters(int count)
    {
        count = Math.Max(1, count);
        TerminalCell[] cells = _lines[_cursorRow].Cells;
        int column = _cursorColumn;
        ClearWidePair(cells, column);
        int shift = Math.Min(count, _columns - column);
        Array.Copy(cells, column + shift, cells, column, _columns - column - shift);
        for (int i = _columns - shift; i < _columns; i++)
        {
            cells[i] = EraseCell;
        }

        SanitizeRightEdge(cells);
        _host.OnScreenChanged();
    }

    /// <summary>Inserts blank lines at the cursor inside the scrolling region.</summary>
    public void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        count = Math.Clamp(count, 1, _scrollBottom - _cursorRow + 1);
        for (int i = 0; i < count; i++)
        {
            CellLine bottom = _lines[_scrollBottom];
            Array.Copy(_lines, _cursorRow, _lines, _cursorRow + 1, _scrollBottom - _cursorRow);
            bottom.Clear(EraseCell);
            _lines[_cursorRow] = bottom;
        }

        _host.OnScreenChanged();
    }

    /// <summary>Deletes lines at the cursor inside the scrolling region.</summary>
    public void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        count = Math.Clamp(count, 1, _scrollBottom - _cursorRow + 1);
        for (int i = 0; i < count; i++)
        {
            CellLine top = _lines[_cursorRow];
            Array.Copy(_lines, _cursorRow + 1, _lines, _cursorRow, _scrollBottom - _cursorRow);
            top.Clear(EraseCell);
            _lines[_scrollBottom] = top;
        }

        _host.OnScreenChanged();
    }

    /// <summary>Scrolls the scrolling region up by <paramref name="count"/> lines.</summary>
    public void ScrollUpRegion(int count)
    {
        ScrollUpInternal(Math.Max(1, count));
        _host.OnScreenChanged();
    }

    /// <summary>Scrolls the scrolling region down by <paramref name="count"/> lines.</summary>
    public void ScrollDownRegion(int count)
    {
        ScrollDownInternal(Math.Max(1, count));
        _host.OnScreenChanged();
    }

    /// <summary>Sets the scrolling region. Invalid regions are ignored.</summary>
    public bool SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, _rows - 1);
        bottom = Math.Clamp(bottom, 0, _rows - 1);
        if (top >= bottom)
        {
            return false;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        _host.OnScreenChanged();
        return true;
    }

    /// <summary>Restores the scrolling region to the whole screen.</summary>
    public void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _host.OnScreenChanged();
    }

    /// <summary>Clears the screen, cursor, margins and tab stops.</summary>
    public void Reset()
    {
        for (int row = 0; row < _rows; row++)
        {
            _lines[row].Clear();
        }

        _cursorRow = 0;
        _cursorColumn = 0;
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _tabStops = CreateTabStops(_columns);
        _host.OnScreenChanged();
    }

    /// <summary>DECALN: fills the screen with a repeated glyph.</summary>
    public void FillScreen(string text)
    {
        var cell = new TerminalCell(text, TerminalColor.Default, TerminalColor.Default, CellAttributes.None, CellWidth.Narrow);
        for (int row = 0; row < _rows; row++)
        {
            Array.Fill(_lines[row].Cells, cell);
        }

        _cursorRow = 0;
        _cursorColumn = 0;
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _host.OnScreenChanged();
    }

    /// <summary>Pads or truncates the screen, keeping the top left content.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns == _columns && rows == _rows)
        {
            return;
        }

        var lines = new CellLine[rows];
        int keep = Math.Min(rows, _rows);
        for (int i = 0; i < keep; i++)
        {
            CellLine line = _lines[i];
            line.Resize(columns);
            lines[i] = line;
        }

        for (int i = keep; i < rows; i++)
        {
            lines[i] = new CellLine(columns);
        }

        _lines = lines;
        _columns = columns;
        _rows = rows;
        _tabStops = CreateTabStops(columns);
        _cursorRow = Math.Min(_cursorRow, _rows - 1);
        _cursorColumn = Math.Min(_cursorColumn, _columns - 1);
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _host.OnScreenChanged();
    }

    private static CellLine[] CreateLines(int columns, int rows)
    {
        var lines = new CellLine[rows];
        for (int i = 0; i < rows; i++)
        {
            lines[i] = new CellLine(columns);
        }

        return lines;
    }

    private static bool[] CreateTabStops(int columns)
    {
        var stops = new bool[columns];
        for (int i = 8; i < columns; i += 8)
        {
            stops[i] = true;
        }

        return stops;
    }

    private void LineFeedInternal()
    {
        if (_cursorRow == _scrollBottom)
        {
            ScrollUpInternal(1);
        }
        else if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }

        _pendingWrap = false;
    }

    private void ScrollUpInternal(int count)
    {
        int height = _scrollBottom - _scrollTop + 1;
        count = Math.Clamp(count, 1, height);
        for (int i = 0; i < count; i++)
        {
            CellLine scrolled = _lines[_scrollTop];
            Array.Copy(_lines, _scrollTop + 1, _lines, _scrollTop, _scrollBottom - _scrollTop);
            CellLine replacement = _scrollTop == 0 && !_isAlternate
                ? _host.OnLineScrolledOff(scrolled)
                : scrolled;
            replacement.Clear(EraseCell);
            _lines[_scrollBottom] = replacement;
        }
    }

    private void ScrollDownInternal(int count)
    {
        int height = _scrollBottom - _scrollTop + 1;
        count = Math.Clamp(count, 1, height);
        for (int i = 0; i < count; i++)
        {
            CellLine bottom = _lines[_scrollBottom];
            Array.Copy(_lines, _scrollTop, _lines, _scrollTop + 1, _scrollBottom - _scrollTop);
            bottom.Clear(EraseCell);
            _lines[_scrollTop] = bottom;
        }
    }

    private void InsertCellsInternal(int row, int column, int count)
    {
        if ((uint)row >= (uint)_rows)
        {
            return;
        }

        TerminalCell[] cells = _lines[row].Cells;
        column = Math.Clamp(column, 0, _columns - 1);
        count = Math.Clamp(count, 1, _columns);
        ClearWidePair(cells, column);
        int shift = Math.Min(count, _columns - column);
        Array.Copy(cells, column, cells, column + shift, _columns - column - shift);
        for (int i = column; i < column + shift; i++)
        {
            cells[i] = EraseCell;
        }

        SanitizeRightEdge(cells);
    }

    private void ClearRow(int row)
    {
        if ((uint)row >= (uint)_rows)
        {
            return;
        }

        Array.Fill(_lines[row].Cells, EraseCell);
    }

    private void ClearCells(int row, int from, int toExclusive)
    {
        if ((uint)row >= (uint)_rows)
        {
            return;
        }

        from = Math.Clamp(from, 0, _columns);
        toExclusive = Math.Clamp(toExclusive, 0, _columns);
        if (from >= toExclusive)
        {
            return;
        }

        TerminalCell[] cells = _lines[row].Cells;
        if (cells[from].Width == CellWidth.Continuation && from > 0)
        {
            from--;
        }

        if (cells[toExclusive - 1].Width == CellWidth.Wide && toExclusive < _columns)
        {
            cells[toExclusive] = EraseCell;
            toExclusive++;
        }

        for (int i = from; i < toExclusive; i++)
        {
            cells[i] = EraseCell;
        }
    }

    private void ClearWidePair(TerminalCell[] cells, int column)
    {
        if ((uint)column >= (uint)_columns)
        {
            return;
        }

        if (cells[column].Width == CellWidth.Continuation)
        {
            if (column > 0)
            {
                cells[column - 1] = EraseCell;
            }
        }
        else if (cells[column].Width == CellWidth.Wide && column + 1 < _columns)
        {
            cells[column + 1] = EraseCell;
        }
    }

    private void SanitizeRightEdge(TerminalCell[] cells)
    {
        int last = _columns - 1;
        if (last < 0)
        {
            return;
        }

        if (cells[last].Width == CellWidth.Wide)
        {
            cells[last] = EraseCell;
        }
        else if (cells[last].Width == CellWidth.Continuation && (last == 0 || cells[last - 1].Width != CellWidth.Wide))
        {
            cells[last] = EraseCell;
        }
    }
}
