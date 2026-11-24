using SurgeLink.Terminal;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Colours of one cell after palette resolution: what the renderer actually draws.</summary>
internal readonly record struct TerminalCellStyle(uint Foreground, uint Background, CellAttributes Attributes);

/// <summary>One highlighted search range on a viewport row.</summary>
internal readonly record struct TerminalSearchSpan(int StartColumn, int EndColumn, bool Current);

/// <summary>Viewport cell range covered by the current selection.</summary>
internal readonly record struct TerminalSelectionRange(TerminalPosition First, TerminalPosition Last, bool HasSelection)
{
    /// <summary>No selection.</summary>
    public static TerminalSelectionRange None => default;

    /// <summary>Builds a range from drag anchors; empty anchors mean no selection.</summary>
    public static TerminalSelectionRange FromAnchors(TerminalPosition? anchor, TerminalPosition? caret)
    {
        if (anchor is not { } start || caret is not { } end || start == end)
        {
            return None;
        }

        var (first, last) = new TerminalSelection(start, end).Normalized();
        return new TerminalSelectionRange(first, last, true);
    }

    /// <summary>True when the cell is inside the highlighted range.</summary>
    public bool Contains(int row, int column)
    {
        if (!HasSelection || row < First.Row || row > Last.Row)
        {
            return false;
        }

        return (row != First.Row || column >= First.Column)
               && (row != Last.Row || column <= Last.Column);
    }
}

/// <summary>
/// Copy of one viewport row taken under the emulator lock. Painting then happens
/// outside the lock, so a slow repaint never stalls the feed thread.
/// </summary>
internal sealed class TerminalRowSnapshot
{
    /// <summary>Text of every column; continuation cells hold an empty string.</summary>
    public string[] Text = Array.Empty<string>();

    /// <summary>Resolved colours of every column.</summary>
    public TerminalCellStyle[] Style = Array.Empty<TerminalCellStyle>();

    /// <summary>Footprint of every column, needed to skip wide glyph halves.</summary>
    public CellWidth[] Width = Array.Empty<CellWidth>();

    /// <summary>True when the cell carries an OSC 8 hyperlink.</summary>
    public bool[] HasHyperlink = Array.Empty<bool>();

    /// <summary>Grows the backing arrays so they can hold <paramref name="columns"/> cells.</summary>
    public void EnsureCapacity(int columns)
    {
        if (Text.Length >= columns)
        {
            return;
        }

        Text = new string[columns];
        Style = new TerminalCellStyle[columns];
        Width = new CellWidth[columns];
        HasHyperlink = new bool[columns];
    }

    /// <summary>Reads one row of the emulator into this snapshot.</summary>
    public void Capture(
        ITerminalEmulator emulator,
        int row,
        int columns,
        TerminalStyleResolver resolver,
        TerminalSelectionRange selection,
        IReadOnlyList<TerminalSearchSpan>? search = null)
    {
        EnsureCapacity(columns);
        for (int column = 0; column < columns; column++)
        {
            TerminalCell cell = emulator.GetCell(row, column);
            TerminalCellStyle style = resolver.Resolve(cell);
            if (selection.Contains(row, column))
            {
                style = style with { Background = resolver.SelectionBackground };
            }
            else if (search is { Count: > 0 } && TryFindSearchSpan(search, column, out bool current))
            {
                style = style with { Background = current ? resolver.SearchCurrentBackground : resolver.SearchBackground };
            }

            Text[column] = (cell.Attributes & CellAttributes.Hidden) != 0 ? " " : cell.Text ?? " ";
            Style[column] = style;
            Width[column] = cell.Width;
            HasHyperlink[column] = !string.IsNullOrEmpty(cell.Hyperlink);
        }
    }

    private static bool TryFindSearchSpan(IReadOnlyList<TerminalSearchSpan> spans, int column, out bool current)
    {
        var found = false;
        for (int i = 0; i < spans.Count; i++)
        {
            TerminalSearchSpan span = spans[i];
            if (column < span.StartColumn || column > span.EndColumn) { continue; }
            if (span.Current)
            {
                current = true;
                return true;
            }

            found = true;
        }

        current = false;
        return found;
    }

    /// <summary>True when both snapshots would paint the same cells.</summary>
    public static bool AreEqual(TerminalRowSnapshot left, TerminalRowSnapshot right, int columns)
    {
        if (left.Text.Length < columns || right.Text.Length < columns)
        {
            return false;
        }

        for (int column = 0; column < columns; column++)
        {
            if (left.Width[column] != right.Width[column]
                || left.Style[column] != right.Style[column]
                || left.HasHyperlink[column] != right.HasHyperlink[column]
                || !string.Equals(left.Text[column], right.Text[column], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
