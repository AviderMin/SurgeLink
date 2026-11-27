using System.Buffers;
using System.Text;

namespace SurgeLink.Terminal;

/// <summary>Computes Unicode- and wide-glyph-aware word boundaries on a visible terminal row.</summary>
public static class TerminalWordSelection
{
    /// <summary>
    /// Returns the inclusive column span of the word containing <paramref name="column"/> on
    /// <paramref name="row"/>, or <c>null</c> when that cell is not part of a word.
    /// </summary>
    public static (int StartColumn, int EndColumn)? FindWordBounds(ITerminalEmulator emulator, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        int rows = emulator.Rows;
        int columns = emulator.Columns;
        if ((uint)row >= (uint)rows || (uint)column >= (uint)columns)
        {
            return null;
        }

        if (emulator.GetCell(row, column).Width == CellWidth.Continuation && column > 0)
        {
            column--;
        }

        if (!IsWordCell(emulator.GetCell(row, column)))
        {
            return null;
        }

        int start = column;
        while (start > 0)
        {
            int candidate = start - 1;
            TerminalCell cell = emulator.GetCell(row, candidate);
            if (cell.Width == CellWidth.Continuation && candidate > 0)
            {
                candidate--;
                cell = emulator.GetCell(row, candidate);
            }

            if (!IsWordCell(cell))
            {
                break;
            }

            start = candidate;
        }

        int end = CellEnd(emulator, row, column, columns);
        while (end + 1 < columns)
        {
            int nextColumn = end + 1;
            TerminalCell cell = emulator.GetCell(row, nextColumn);
            if (!IsWordCell(cell))
            {
                break;
            }

            end = CellEnd(emulator, row, nextColumn, columns);
        }

        return (start, end);
    }

    private static int CellEnd(ITerminalEmulator emulator, int row, int cellStart, int columns) =>
        cellStart + 1 < columns && emulator.GetCell(row, cellStart + 1).Width == CellWidth.Continuation
            ? cellStart + 1
            : cellStart;

    private static bool IsWordCell(TerminalCell cell)
    {
        if (string.IsNullOrEmpty(cell.Text))
        {
            return false;
        }

        return Rune.DecodeFromUtf16(cell.Text, out Rune rune, out _) == OperationStatus.Done
            && (Rune.IsLetterOrDigit(rune)
                || (rune.IsAscii && (char)rune.Value is '_' or '-' or '.' or '/' or '~' or '@' or ':'));
    }
}
