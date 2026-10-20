namespace SurgeLink.Terminal;

/// <summary>
/// One row of cells. Rows are moved by reference between the screen, the
/// alternate screen and the scrollback, so they are a class rather than a
/// value type.
/// </summary>
internal sealed class CellLine
{
    public CellLine(int columns)
    {
        Cells = new TerminalCell[columns];
        Array.Fill(Cells, TerminalCell.Blank);
    }

    /// <summary>The cells of this row; always <see cref="Length"/> entries.</summary>
    public TerminalCell[] Cells { get; private set; }

    /// <summary>Number of columns currently stored.</summary>
    public int Length => Cells.Length;

    /// <summary>Resets every cell to blank.</summary>
    public void Clear() => Array.Fill(Cells, TerminalCell.Blank);

    /// <summary>Pads or truncates the row to the requested width.</summary>
    public void Resize(int columns)
    {
        if (columns == Cells.Length)
        {
            return;
        }

        var resized = new TerminalCell[columns];
        Array.Fill(resized, TerminalCell.Blank);
        int copy = Math.Min(columns, Cells.Length);
        Array.Copy(Cells, resized, copy);
        if (columns > 0 && resized[columns - 1].Width == CellWidth.Wide)
        {
            resized[columns - 1] = TerminalCell.Blank;
        }

        Cells = resized;
    }
}
