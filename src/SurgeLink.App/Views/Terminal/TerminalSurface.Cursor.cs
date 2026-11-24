using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.Terminal;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Cursor half of <see cref="TerminalSurface"/>: block, bar or underline.</summary>
internal sealed partial class TerminalSurface
{
    /// <summary>
    /// Draws the cursor. A block cursor repaints the glyph underneath in the
    /// background colour instead of covering it, which is what a real terminal does.
    /// </summary>
    private void PaintCursor(TerminalMetrics metrics, TerminalStyleResolver resolver)
    {
        var emulator = _bridge.Emulator;
        TerminalCursor cursor;
        TerminalModes modes;
        TerminalCell cell;
        int viewportRow;

        lock (emulator.SyncRoot)
        {
            cursor = emulator.Cursor;
            modes = emulator.Modes;
            // Cursor reports a screen row; the paint surface addresses viewport
            // rows, which are shifted down by the history the user scrolled back.
            viewportRow = cursor.Row + emulator.ViewportOffset;
            cell = emulator.GetCell(viewportRow, cursor.Column);
        }

        bool visible = modes.CursorVisible
            && !_bridge.IsClosed
            && cursor.Visible
            && _hasFocus
            && viewportRow >= 0 && viewportRow < _paintedRowCount
            && cursor.Column >= 0 && cursor.Column < _paintedColumns;

        if (!visible)
        {
            _cursorLayer.Visibility = Visibility.Collapsed;
            return;
        }

        _cursorLayer.Visibility = Visibility.Visible;
        _cursorLayer.Opacity = !modes.CursorBlink || _blinkOn ? 1 : 0;

        double left = cursor.Column * metrics.CellWidth;
        double top = viewportRow * metrics.CellHeight;
        var brush = resolver.Brush(resolver.Cursor);

        _cursorRect.Fill = brush;
        _cursorRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(_cursorRect, left);
        Canvas.SetTop(_cursorRect, top);

        double cursorWidth = cell.Width == CellWidth.Wide ? metrics.CellWidth * 2 : metrics.CellWidth;
        switch (modes.CursorShape)
        {
            case TerminalCursorShape.Bar:
                _cursorGlyph.Visibility = Visibility.Collapsed;
                _cursorRect.Width = Math.Max(2, metrics.CellWidth * 0.15);
                _cursorRect.Height = metrics.CellHeight;
                break;

            case TerminalCursorShape.Underline:
                _cursorGlyph.Visibility = Visibility.Collapsed;
                _cursorRect.Width = cursorWidth;
                _cursorRect.Height = Math.Max(2, metrics.CellHeight * 0.12);
                Canvas.SetTop(_cursorRect, top + metrics.CellHeight - _cursorRect.Height);
                break;

            default:
                _cursorRect.Width = cursorWidth;
                _cursorRect.Height = metrics.CellHeight;
                _cursorGlyph.Text = cell.Text ?? " ";
                _cursorGlyph.Foreground = resolver.Brush(resolver.DefaultBackground);
                Canvas.SetLeft(_cursorGlyph, left);
                Canvas.SetTop(_cursorGlyph, top);
                _cursorGlyph.Visibility = Visibility.Visible;
                break;
        }
    }
}
