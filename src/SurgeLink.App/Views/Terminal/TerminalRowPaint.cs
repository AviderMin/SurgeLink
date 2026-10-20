using Microsoft.UI.Xaml.Controls;
using SurgeLink.Terminal;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Colours of one cell after palette resolution: what the renderer actually draws.</summary>
internal readonly record struct TerminalCellStyle(uint Foreground, uint Background, CellAttributes Attributes);

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
    }

    /// <summary>Reads one row of the emulator into this snapshot.</summary>
    public void Capture(
        ITerminalEmulator emulator,
        int row,
        int columns,
        TerminalStyleResolver resolver,
        TerminalSelectionRange selection)
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

            Text[column] = (cell.Attributes & CellAttributes.Hidden) != 0 ? " " : cell.Text ?? " ";
            Style[column] = style;
            Width[column] = cell.Width;
        }
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
                || !string.Equals(left.Text[column], right.Text[column], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The XAML elements currently showing one row. They are reused on every repaint
/// (created on demand, hidden when a row needs fewer of them) so steady state
/// repaints do not allocate UIElements.
/// </summary>
internal sealed class TerminalRowVisuals
{
    /// <summary>Background bands, drawn below all text.</summary>
    public List<Rectangle> Backgrounds { get; } = new(4);

    /// <summary>Text runs of the row.</summary>
    public List<TextBlock> Runs { get; } = new(4);
}
