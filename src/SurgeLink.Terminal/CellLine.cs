namespace SurgeLink.Terminal;

/// <summary>
/// One row of cells. Rows are moved by reference between the screen, the
/// alternate screen and the scrollback, so they are a class rather than a
/// value type.
/// </summary>
internal sealed class CellLine
{
    private int _columns;
    private bool _hiddenDirty;

    public CellLine(int columns)
    {
        _columns = columns;
        Cells = new TerminalCell[columns];
        Array.Fill(Cells, TerminalCell.Blank);
    }

    /// <summary>
    /// Backing cells. The array may be wider than <see cref="Length"/> so that
    /// narrowing a window does not permanently discard hidden history; callers
    /// must treat indices at or beyond <see cref="Length"/> as invisible.
    /// </summary>
    public TerminalCell[] Cells { get; private set; }

    /// <summary>Number of columns currently visible on the screen.</summary>
    public int Length => _columns;

    /// <summary>Resets every cell, including hidden cells, to blank.</summary>
    public void Clear()
    {
        Array.Fill(Cells, TerminalCell.Blank);
        _hiddenDirty = false;
    }

    public void Clear(TerminalCell blank)
    {
        // Filling the entire backing array, including hidden cells, leaves no
        // stale content for a later widening to resurrect.
        Array.Fill(Cells, blank);
        _hiddenDirty = false;
    }

    /// <summary>
    /// Reads one cell as it can be displayed at the current width. A wide glyph
    /// cut by the right edge is reported as blank rather than as a lone Wide cell
    /// whose continuation is hidden; history storage itself is never altered here.
    /// </summary>
    public TerminalCell GetCell(int column)
    {
        if ((uint)column >= (uint)_columns)
        {
            return TerminalCell.Blank;
        }

        TerminalCell cell = Cells[column];
        if (cell.Width == CellWidth.Wide && column + 1 >= _columns)
        {
            return TerminalCell.Blank;
        }

        return cell;
    }

    /// <summary>Drops cells that are currently hidden to the right of the visible row.</summary>
    public void ClearHidden()
    {
        if (!_hiddenDirty)
        {
            return;
        }

        if (Cells.Length > _columns)
        {
            Array.Fill(Cells, TerminalCell.Blank, _columns, Cells.Length - _columns);
        }

        _hiddenDirty = false;
    }

    /// <summary>
    /// Makes the visible row safe for an active-screen edit. A wide glyph cut by
    /// the current right edge is removed together with its hidden continuation,
    /// and all other hidden cells are invalidated before the edit moves cells.
    /// </summary>
    public void PrepareForEdit()
    {
        if (_columns > 0 && Cells[_columns - 1].Width == CellWidth.Wide)
        {
            Cells[_columns - 1] = TerminalCell.Blank;
            if (_columns < Cells.Length)
            {
                Cells[_columns] = TerminalCell.Blank;
            }
        }

        ClearHidden();
    }

    /// <summary>
    /// Changes the visible width of the row. Narrowing keeps the full backing
    /// cells so widening can restore the original text; partially visible wide
    /// glyphs are handled by <see cref="GetCell"/> without mutating storage.
    /// </summary>
    public void Resize(int columns)
    {
        if (columns == _columns)
        {
            return;
        }

        var narrowing = columns < _columns;
        if (columns > Cells.Length)
        {
            var grown = new TerminalCell[columns];
            Array.Fill(grown, TerminalCell.Blank);
            // Copy the whole backing array, not just the visible prefix: hidden
            // cells from an earlier narrow window belong to the original line.
            Array.Copy(Cells, grown, Cells.Length);
            Cells = grown;
        }

        _columns = columns;
        if (narrowing)
        {
            // Cells between the new and old right edges may still hold text.
            // Clear them once, on the next edit, instead of on every print.
            _hiddenDirty = true;
        }
    }
}
