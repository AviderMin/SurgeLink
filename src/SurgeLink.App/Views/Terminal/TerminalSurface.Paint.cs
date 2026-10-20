using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.Terminal;
using Windows.UI.Text;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Painting half of <see cref="TerminalSurface"/>: snapshot the grid, diff it row by
/// row, and update only the visuals of rows that changed.
/// </summary>
internal sealed partial class TerminalSurface
{
    /// <summary>Paints everything that changed since the last frame.</summary>
    private void Paint()
    {
        _paintQueued = false;
        TerminalMetrics? metrics = _metrics;
        TerminalStyleResolver? resolver = _resolver;
        if (!_loaded || metrics is null || resolver is null)
        {
            return;
        }

        var emulator = _bridge.Emulator;
        int visibleColumns;
        int visibleRows;
        long version;
        bool repaintAll = _allRowsDirty;
        var selection = TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret);

        lock (emulator.SyncRoot)
        {
            visibleColumns = Math.Max(1, Math.Min(emulator.Columns, _requestedColumns > 0 ? _requestedColumns : emulator.Columns));
            visibleRows = Math.Min(emulator.Rows, _scratchRows.Length);
            version = emulator.Version;

            if (!_forcePaint && !repaintAll && version == _paintedVersion && visibleColumns == _paintedColumns && visibleRows == _paintedRowCount)
            {
                return;
            }

            for (int row = 0; row < visibleRows; row++)
            {
                _scratchRows[row].Capture(emulator, row, visibleColumns, resolver, selection);
            }
        }

        repaintAll |= visibleColumns != _paintedColumns;

        for (int row = 0; row < visibleRows; row++)
        {
            if (!repaintAll && TerminalRowSnapshot.AreEqual(_paintedRows[row], _scratchRows[row], visibleColumns))
            {
                continue;
            }

            PaintRow(row, _scratchRows[row], visibleColumns, metrics, resolver);

            // The scratch row is now the painted row; the old snapshot becomes scratch.
            (_paintedRows[row], _scratchRows[row]) = (_scratchRows[row], _paintedRows[row]);
        }

        for (int row = visibleRows; row < _rowVisuals.Length; row++)
        {
            HideRow(_rowVisuals[row]);
        }

        _paintedColumns = visibleColumns;
        _paintedRowCount = visibleRows;
        _paintedVersion = version;
        _forcePaint = false;
        _allRowsDirty = false;

        PaintCursor(metrics, resolver);
        LogLayoutOnce();
    }

    private void PaintRow(int row, TerminalRowSnapshot snapshot, int columns, TerminalMetrics metrics, TerminalStyleResolver resolver)
    {
        var visuals = _rowVisuals[row];
        double top = row * metrics.CellHeight;

        int backgroundCount = PaintBackgrounds(visuals, snapshot, columns, metrics, resolver, top);
        int runCount = PaintRuns(visuals, snapshot, columns, metrics, resolver, top);

        for (int i = backgroundCount; i < visuals.Backgrounds.Count; i++)
        {
            visuals.Backgrounds[i].Visibility = Visibility.Collapsed;
        }

        for (int i = runCount; i < visuals.Runs.Count; i++)
        {
            visuals.Runs[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Emits one rectangle per run of cells sharing a non-default background.</summary>
    private int PaintBackgrounds(
        TerminalRowVisuals visuals,
        TerminalRowSnapshot snapshot,
        int columns,
        TerminalMetrics metrics,
        TerminalStyleResolver resolver,
        double top)
    {
        int count = 0;
        int column = 0;
        uint transparent = resolver.DefaultBackground;

        while (column < columns)
        {
            uint background = snapshot.Style[column].Background;
            if (background == transparent)
            {
                column++;
                continue;
            }

            int start = column;
            while (column < columns && snapshot.Style[column].Background == background)
            {
                column++;
            }

            Rectangle rect = GetBackground(visuals, count++);
            rect.Fill = resolver.Brush(background);
            rect.Width = (column - start) * metrics.CellWidth;
            rect.Height = metrics.CellHeight;
            Canvas.SetLeft(rect, start * metrics.CellWidth);
            Canvas.SetTop(rect, top);
            rect.Visibility = Visibility.Visible;
        }

        return count;
    }

    /// <summary>Emits one text block per run of cells sharing a foreground style.</summary>
    private int PaintRuns(
        TerminalRowVisuals visuals,
        TerminalRowSnapshot snapshot,
        int columns,
        TerminalMetrics metrics,
        TerminalStyleResolver resolver,
        double top)
    {
        int count = 0;
        int column = 0;

        while (column < columns)
        {
            CellWidth width = snapshot.Width[column];
            if (width == CellWidth.Continuation)
            {
                // The right half of a wide glyph carries no text of its own; the wide
                // cell that owns it already painted the glyph across both columns.
                column++;
                continue;
            }

            TerminalCellStyle style = snapshot.Style[column];
            if (width == CellWidth.Wide)
            {
                // A wide glyph is painted on its own so a proportional fallback cannot
                // shift the runs that follow it.
                count = EmitRun(visuals, count, column, snapshot.Text[column], style, metrics, resolver, top);
                column += 2;
                continue;
            }

            int start = column;
            _runBuilder.Clear();
            while (column < columns && snapshot.Width[column] == CellWidth.Narrow && snapshot.Style[column] == style)
            {
                _runBuilder.Append(snapshot.Text[column]);
                column++;
            }

            count = EmitRun(visuals, count, start, _runBuilder.ToString(), style, metrics, resolver, top);
        }

        return count;
    }

    private int EmitRun(
        TerminalRowVisuals visuals,
        int index,
        int column,
        string text,
        TerminalCellStyle style,
        TerminalMetrics metrics,
        TerminalStyleResolver resolver,
        double top)
    {
        if (text.Length == 0 && style.Attributes == CellAttributes.None)
        {
            // Continuation half of a wide glyph: nothing to draw.
            return index;
        }

        TextDecorations decorations = default;
        if ((style.Attributes & CellAttributes.Underline) != 0)
        {
            decorations |= TextDecorations.Underline;
        }

        if ((style.Attributes & CellAttributes.Strikethrough) != 0)
        {
            decorations |= TextDecorations.Strikethrough;
        }

        if (decorations == default && IsBlank(text))
        {
            // Blank cells cost nothing: the background band already painted them.
            return index;
        }

        TextBlock block = GetRun(visuals, index);
        block.Text = text;
        block.Foreground = resolver.Brush(style.Foreground);
        block.FontWeight = (style.Attributes & CellAttributes.Bold) != 0 ? FontWeights.Bold : FontWeights.Normal;
        block.FontStyle = (style.Attributes & CellAttributes.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
        block.TextDecorations = decorations;
        Canvas.SetLeft(block, column * metrics.CellWidth);
        Canvas.SetTop(block, top);
        block.Visibility = Visibility.Visible;
        return index + 1;
    }

    private static bool IsBlank(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ')
            {
                return false;
            }
        }

        return true;
    }

    private TextBlock GetRun(TerminalRowVisuals visuals, int index)
    {
        while (visuals.Runs.Count <= index)
        {
            var block = new TextBlock
            {
                IsHitTestVisible = false,
                IsTextScaleFactorEnabled = false,
                TextWrapping = TextWrapping.NoWrap,
            };

            StyleRun(block, _metrics!);
            visuals.Runs.Add(block);
            _textLayer.Children.Add(block);
        }

        return visuals.Runs[index];
    }

    private Rectangle GetBackground(TerminalRowVisuals visuals, int index)
    {
        while (visuals.Backgrounds.Count <= index)
        {
            var rect = new Rectangle { IsHitTestVisible = false };
            visuals.Backgrounds.Add(rect);
            _backgroundLayer.Children.Add(rect);
        }

        return visuals.Backgrounds[index];
    }

    private void HideRow(TerminalRowVisuals visuals)
    {
        foreach (var run in visuals.Runs)
        {
            run.Visibility = Visibility.Collapsed;
        }

        foreach (var background in visuals.Backgrounds)
        {
            background.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Logs the live element count once per distinct layout. This is the number that
    /// proves the renderer never creates one element per cell.
    /// </summary>
    private void LogLayoutOnce()
    {
        if (_logger is null)
        {
            return;
        }

        int runs = 0;
        int bands = 0;
        for (int row = 0; row < _paintedRowCount && row < _rowVisuals.Length; row++)
        {
            var visuals = _rowVisuals[row];
            for (int i = 0; i < visuals.Runs.Count; i++)
            {
                if (visuals.Runs[i].Visibility == Visibility.Visible)
                {
                    runs++;
                }
            }

            for (int i = 0; i < visuals.Backgrounds.Count; i++)
            {
                if (visuals.Backgrounds[i].Visibility == Visibility.Visible)
                {
                    bands++;
                }
            }
        }

        string signature = _paintedColumns + "x" + _paintedRowCount + ":" + runs + ":" + bands;
        if (string.Equals(signature, _loggedLayout, StringComparison.Ordinal))
        {
            return;
        }

        _loggedLayout = signature;
        _logger.LogInformation(
            "Terminal painted {Columns}x{Rows} grid with {Runs} text runs, {Bands} background bands, {Elements} elements total",
            _paintedColumns,
            _paintedRowCount,
            runs,
            bands,
            runs + bands);
    }
}
