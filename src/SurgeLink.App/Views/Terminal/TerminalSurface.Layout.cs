using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Layout and grid-size half of <see cref="TerminalSurface"/>.</summary>
internal sealed partial class TerminalSurface
{
    /// <summary>
    /// Claims every pixel the host offers. A Canvas-based surface reports zero
    /// desired size on its own, and a host that aligns content to its desired size
    /// would then collapse the terminal to 0x0; reporting the available size keeps
    /// the control honest whatever the hosting template does.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A Canvas reports no desired size, so size it explicitly; this keeps the
        // surface correct even if a host template chooses not to stretch content.
        _root.Width = Math.Max(0, ActualWidth);
        _root.Height = Math.Max(0, ActualHeight);
        UpdateGridSize();
    }

    private void UpdateGridSize(bool immediate = false)
    {
        if (!_active || !_loaded || _metrics is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        int columns = Math.Max(1, (int)Math.Floor(Math.Max(0, ActualWidth - 2 * ContentInset - ScrollBarWidth) / _metrics.CellWidth));
        int rows = Math.Max(1, (int)Math.Floor(Math.Max(0, ActualHeight - 2 * ContentInset) / _metrics.CellHeight));
        if (!immediate && _requestedColumns > 0 && columns != _requestedColumns && _resizeTimer is { } timer)
        {
            if (!timer.IsRunning) { timer.Start(); }
            return;
        }
        _resizeTimer?.Stop();
        if (columns == _requestedColumns && rows == _requestedRows && _paintedRows.Length == rows)
        {
            return;
        }

        _requestedColumns = columns;
        _requestedRows = rows;
        EnsureRowBuffers(rows);
        _allRowsDirty = true;
        _logger?.LogInformation("Terminal grid resized to {Columns}x{Rows} cells", columns, rows);
        _ = ResizeRemoteAsync(columns, rows);
        InvalidatePaint(force: true);
    }

    private async Task ResizeRemoteAsync(int columns, int rows)
    {
        try
        {
            // Resizes the emulator and tells the remote PTY, so the shell reflows with us.
            await _bridge.ResizeAsync(columns, rows);
        }
        catch (Exception)
        {
            // A closed channel must not surface as a crash in a resize handler.
        }

        InvalidatePaint(force: true);
    }

    private void EnsureRowBuffers(int rows)
    {
        if (_paintedRows.Length == rows)
        {
            return;
        }

        for (var index = rows; index < _paintedRows.Length; index++) { _paintedRows[index].ReleaseTextLayouts(); }
        _paintedRows = ResizeRowArray(_paintedRows, rows);
        _scratchRows = ResizeRowArray(_scratchRows, rows);
    }

    private static T[] ResizeRowArray<T>(T[] current, int length)
        where T : class, new()
    {
        var next = new T[length];
        for (int i = 0; i < length; i++)
        {
            next[i] = i < current.Length ? current[i] : new T();
        }

        return next;
    }
}
