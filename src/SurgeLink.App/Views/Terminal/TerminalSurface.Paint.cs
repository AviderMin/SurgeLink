using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SurgeLink.Terminal;
using Windows.Foundation;
using Windows.UI;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// GPU-accelerated Direct2D/DirectWrite painting half of <see cref="TerminalSurface"/>
/// powered by Win2D <see cref="CanvasControl"/>. Captures immutable row snapshots under
/// the emulator lock and renders merged background bands, text runs and decorations
/// directly in one draw pass with zero per-row XAML elements.
/// </summary>
internal sealed partial class TerminalSurface
{
    /// <summary>Captures any changed rows and triggers a Win2D frame repaint.</summary>
    private void Paint()
    {
        _paintQueued = false;
        TerminalMetrics? metrics = _metrics;
        TerminalStyleResolver? resolver = _resolver;
        if (!_active || !_loaded || metrics is null || resolver is null)
        {
            return;
        }

        var emulator = _bridge.Emulator;
        int visibleColumns;
        int visibleRows;
        long version;
        int scrollback;
        int viewportOffset;
        int rows;
        bool captureRows;
        bool repaintAll = _allRowsDirty;
        var selection = TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret);
        Dictionary<int, List<TerminalSearchSpan>>? searchRows = null;

        // A background search will retry the frame after releasing the emulator.
        if (_searchRunning && !System.Threading.Monitor.TryEnter(emulator.SyncRoot))
        {
            _searchPaintDeferred = true;
            return;
        }
        if (!_searchRunning) { System.Threading.Monitor.Enter(emulator.SyncRoot); }
        try
        {
            if (!ReferenceEquals(resolver.Palette, emulator.Palette))
            {
                resolver = new TerminalStyleResolver(emulator.Palette);
                repaintAll = true;
            }
            visibleColumns = Math.Max(1, Math.Min(emulator.Columns, _requestedColumns > 0 ? _requestedColumns : emulator.Columns));
            visibleRows = Math.Min(emulator.Rows, _scratchRows.Length);
            version = emulator.Version;
            searchRows = BuildSearchHighlights(emulator, visibleRows);
            scrollback = emulator.ScrollbackLength;
            viewportOffset = emulator.ViewportOffset;
            rows = emulator.Rows;
            captureRows = _forcePaint || repaintAll || version != _paintedVersion
                || visibleColumns != _paintedColumns || visibleRows != _paintedRowCount;

            for (int row = 0; captureRows && row < visibleRows; row++)
            {
                IReadOnlyList<TerminalSearchSpan>? spans = null;
                if (searchRows is not null && searchRows.TryGetValue(row, out List<TerminalSearchSpan>? rowSpans))
                {
                    spans = rowSpans;
                }

                _scratchRows[row].Capture(emulator, row, visibleColumns, resolver, selection, spans);
            }
        }
        finally { System.Threading.Monitor.Exit(emulator.SyncRoot); }

        if (!ReferenceEquals(resolver, _resolver)) { ApplyPalette(resolver); }
        _updatingScrollBar = true;
        try
        {
            _scrollBar.Maximum = scrollback;
            _scrollBar.ViewportSize = rows;
            _scrollBar.LargeChange = Math.Max(1, rows - 1);
            _scrollBar.Value = scrollback - viewportOffset;
            _scrollBar.IsEnabled = scrollback > 0;
        }
        finally { _updatingScrollBar = false; }

        if (!captureRows) { return; }

        repaintAll |= visibleColumns != _paintedColumns || visibleRows != _paintedRowCount;
        bool anyRowChanged = repaintAll;
        bool hasBlinking = false;

        for (int row = 0; row < visibleRows; row++)
        {
            if (repaintAll || !TerminalRowSnapshot.AreEqual(_paintedRows[row], _scratchRows[row], visibleColumns))
            {
                _paintedRows[row].ReleaseTextLayouts();
                (_paintedRows[row], _scratchRows[row]) = (_scratchRows[row], _paintedRows[row]);
                anyRowChanged = true;
            }

            if (!hasBlinking)
            {
                var styles = _paintedRows[row].Style;
                for (int col = 0; col < visibleColumns; col++)
                {
                    if ((styles[col].Attributes & CellAttributes.Blink) != 0)
                    {
                        hasBlinking = true;
                        break;
                    }
                }
            }
        }

        _hasBlinkingCells = hasBlinking;
        _paintedColumns = visibleColumns;
        _paintedRowCount = visibleRows;
        _paintedVersion = version;
        _forcePaint = false;
        _allRowsDirty = false;

        if (anyRowChanged)
        {
            _canvas?.Invalidate();
        }

        UpdateBlink(PaintCursor(metrics, resolver));
        LogLayoutOnce();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        TerminalMetrics? metrics = _metrics;
        TerminalStyleResolver? resolver = _resolver;
        if (!_active || metrics is null || resolver is null || _paintedRowCount <= 0 || _paintedColumns <= 0)
        {
            return;
        }

        CanvasDrawingSession ds = args.DrawingSession;
        ds.Clear(ToColor(resolver.DefaultBackground));
        ds.TextAntialiasing = CanvasTextAntialiasing.ClearType;

        float cellWidth = (float)metrics.CellWidth;
        float cellHeight = (float)metrics.CellHeight;
        int columns = _paintedColumns;
        int rows = Math.Min(_paintedRowCount, _paintedRows.Length);
        uint defaultBackground = resolver.DefaultBackground;

        // Pass 1: merged horizontal background bands.
        for (int row = 0; row < rows; row++)
        {
            TerminalRowSnapshot snapshot = _paintedRows[row];
            if (snapshot.Style.Length < columns) continue;
            float top = row * cellHeight;
            int col = 0;
            while (col < columns)
            {
                uint bg = snapshot.Style[col].Background;
                if (bg == defaultBackground)
                {
                    col++;
                    continue;
                }

                int start = col;
                col++;
                while (col < columns && snapshot.Style[col].Background == bg)
                {
                    col++;
                }

                ds.FillRectangle(
                    new Rect(start * cellWidth, top, (col - start) * cellWidth + 0.5f, cellHeight + 0.5f),
                    ToColor(bg));
            }
        }

        // Pass 2: merged text runs and cell decorations (underline, strikethrough, hyperlinks).
        for (int row = 0; row < rows; row++)
        {
            TerminalRowSnapshot snapshot = _paintedRows[row];
            if (snapshot.Text.Length < columns) continue;
            float top = row * cellHeight;
            DrawRowTextAndDecorations(sender, ds, snapshot, columns, cellWidth, cellHeight, top);
        }
    }

    private void DrawRowTextAndDecorations(
        CanvasControl canvas,
        CanvasDrawingSession ds,
        TerminalRowSnapshot snapshot,
        int columns,
        float cellWidth,
        float cellHeight,
        float top)
    {
        int col = 0;
        while (col < columns)
        {
            if (snapshot.Width[col] == CellWidth.Continuation)
            {
                col++;
                continue;
            }

            TerminalCellStyle style = snapshot.Style[col];
            bool hasLink = snapshot.HasHyperlink[col];
            bool blinkHidden = (style.Attributes & CellAttributes.Blink) != 0 && !_blinkOn;
            string cellText = snapshot.Text[col];

            // Wide or multi-char (combining/emoji) glyphs are placed at their exact cell
            // coordinate so DirectWrite shaping never shifts following columns off-grid.
            bool isSingleAsciiCell = snapshot.Width[col] == CellWidth.Narrow
                && cellText.Length == 1
                && cellText[0] < 0x80;

            int start = col;
            if (isSingleAsciiCell)
            {
                bool hasText = cellText[0] != ' ';
                col++;
                while (col < columns
                    && snapshot.Width[col] == CellWidth.Narrow
                    && snapshot.Style[col].Foreground == style.Foreground
                    && snapshot.Style[col].Underline == style.Underline
                    && SameTextStyle(snapshot.Style[col].Attributes, style.Attributes)
                    && snapshot.HasHyperlink[col] == hasLink)
                {
                    string nextText = snapshot.Text[col];
                    if (nextText.Length != 1 || nextText[0] >= 0x80) break;
                    hasText |= nextText[0] != ' ';
                    col++;
                }

                if (!blinkHidden && hasText)
                {
                    string? runText = null;
                    if (snapshot.TextLayouts?.ContainsKey(start) != true)
                    {
                        _runBuilder.Clear();
                        for (var index = start; index < col; index++) { _runBuilder.Append(snapshot.Text[index]); }
                        runText = _runBuilder.ToString();
                    }
                    DrawGlyphRun(canvas, ds, snapshot, start, runText, start * cellWidth, top, style);
                }

                DrawDecorations(ds, start, col - start, cellWidth, cellHeight, top, style, hasLink, blinkHidden);
            }
            else
            {
                int spanCols = snapshot.Width[col] == CellWidth.Wide ? 2 : 1;
                col += spanCols;
                if (!blinkHidden && !IsBlank(cellText))
                {
                    DrawGlyphRun(canvas, ds, snapshot, start, cellText, start * cellWidth, top, style);
                }

                DrawDecorations(ds, start, spanCols, cellWidth, cellHeight, top, style, hasLink, blinkHidden);
            }
        }
    }

    private void DrawGlyphRun(
        CanvasControl canvas,
        CanvasDrawingSession ds,
        TerminalRowSnapshot row,
        int column,
        string? text,
        float left,
        float top,
        TerminalCellStyle style)
    {
        if (row.TextLayouts?.TryGetValue(column, out var cached) == true)
        {
            ds.DrawTextLayout(cached, left, top, ToColor(style.Foreground));
            return;
        }
        if (text is null) { return; }

        bool bold = (style.Attributes & CellAttributes.Bold) != 0;
        bool italic = (style.Attributes & CellAttributes.Italic) != 0;
        CanvasTextFormat? format = (bold, italic) switch
        {
            (true, true) => _formatBoldItalic,
            (true, false) => _formatBold,
            (false, true) => _formatItalic,
            _ => _formatRegular,
        };
        if (format is null) return;

        Color color = ToColor(style.Foreground);
        var layouts = row.TextLayouts ??= new();
        if (layouts.Count >= 128)
        {
            ds.DrawText(text, left, top, color, format);
            return;
        }
        var layout = new CanvasTextLayout(canvas, text, format, 0, 0);
        layouts.Add(column, layout);
        ds.DrawTextLayout(layout, left, top, color);
    }

    private static void DrawDecorations(
        CanvasDrawingSession ds,
        int startCol,
        int spanCols,
        float cellWidth,
        float cellHeight,
        float top,
        TerminalCellStyle style,
        bool hasLink,
        bool blinkHidden)
    {
        if (blinkHidden) return;
        bool underline = (style.Attributes & CellAttributes.Underline) != 0 || hasLink;
        bool strike = (style.Attributes & CellAttributes.Strikethrough) != 0;
        if (!underline && !strike) return;

        float x0 = startCol * cellWidth;
        float x1 = (startCol + spanCols) * cellWidth;
        if (underline)
        {
            float y = top + cellHeight - 1.5f;
            ds.DrawLine(new Vector2(x0, y), new Vector2(x1, y), ToColor(style.Underline), 1f);
        }
        if (strike)
        {
            float y = top + cellHeight * 0.55f;
            ds.DrawLine(new Vector2(x0, y), new Vector2(x1, y), ToColor(style.Foreground), 1f);
        }
    }

    private static bool SameTextStyle(CellAttributes left, CellAttributes right)
    {
        const CellAttributes mask = CellAttributes.Bold | CellAttributes.Italic
            | CellAttributes.Underline | CellAttributes.Strikethrough | CellAttributes.Blink;
        return (left & mask) == (right & mask);
    }

    private static Color ToColor(uint rgb) => Color.FromArgb(
        255,
        (byte)((rgb >> 16) & 0xFF),
        (byte)((rgb >> 8) & 0xFF),
        (byte)(rgb & 0xFF));

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

    /// <summary>Logs the live grid dimensions once per distinct layout.</summary>
    private void LogLayoutOnce()
    {
        if (_logger is null)
        {
            return;
        }

        string signature = _paintedColumns + "x" + _paintedRowCount + ":win2d";
        if (string.Equals(signature, _loggedLayout, StringComparison.Ordinal))
        {
            return;
        }

        _loggedLayout = signature;
        _logger.LogDebug(
            "Terminal rendering {Columns}x{Rows} grid via Win2D Direct2D/DirectWrite",
            _paintedColumns,
            _paintedRowCount);
    }
}
