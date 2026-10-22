using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SurgeLink.App.Services;
using SurgeLink.Terminal;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

using SurgeLink.App.Localization;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Pointer half of <see cref="TerminalSurface"/>: selection, wheel scrollback,
/// clipboard and the right-click menu.
/// </summary>
internal sealed partial class TerminalSurface
{
    private const double MinFontSize = 6;
    private const double MaxFontSize = 48;
    private const double DefaultFontSize = 13;

    private bool HasSelection => TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret).HasSelection;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != this;
             source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source))
        {
            if (source is ScrollBar) { return; }
        }
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            ShowContextMenu(point.Position);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        TerminalPosition cell = CellFromPoint(point.Position);
        if (IsKeyDown(VirtualKey.Shift) && HasSelection)
        {
            // Shift+click extends the existing selection instead of restarting it.
            _selectionCaret = cell;
        }
        else
        {
            _selectionAnchor = cell;
            _selectionCaret = cell;
        }

        _selecting = true;
        CapturePointer(e.Pointer);
        InvalidatePaint(force: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        TerminalPosition cell = CellFromPoint(e.GetCurrentPoint(this).Position);
        if (cell == _selectionCaret)
        {
            return;
        }

        _selectionCaret = cell;
        InvalidatePaint(force: true);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _selecting = false;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SelectWordAt(CellFromPoint(e.GetPosition(this)));
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (IsKeyDown(VirtualKey.Control))
        {
            ZoomFont(delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        int lines = (delta / 120) * LinesPerWheelNotch;
        if (lines != 0)
        {
            ScrollHistory(lines);
            if (_selecting) { _selectionCaret = CellFromPoint(point.Position); }
            InvalidatePaint(force: true);
        }

        e.Handled = true;
    }

    private TerminalPosition CellFromPoint(Point point)
    {
        var emulator = _bridge.Emulator;
        int rows;
        int columns;
        lock (emulator.SyncRoot)
        {
            rows = emulator.Rows;
            columns = emulator.Columns;
        }

        double cellWidth = _metrics?.CellWidth ?? 8;
        double cellHeight = _metrics?.CellHeight ?? 16;
        int column = (int)Math.Floor((point.X - ContentInset) / cellWidth);
        int row = (int)Math.Floor((point.Y - ContentInset) / cellHeight);
        return new TerminalPosition(
            Math.Clamp(row, 0, Math.Max(0, rows - 1)),
            Math.Clamp(column, 0, Math.Max(0, columns - 1)));
    }

    private void SelectWordAt(TerminalPosition position)
    {
        var emulator = _bridge.Emulator;
        int columns;
        var word = new List<bool>(128);

        lock (emulator.SyncRoot)
        {
            if (position.Row < 0 || position.Row >= emulator.Rows)
            {
                return;
            }

            columns = emulator.Columns;
            for (int column = 0; column < columns; column++)
            {
                string text = emulator.GetCell(position.Row, column).Text ?? string.Empty;
                word.Add(text.Length == 1 && IsWordCharacter(text[0]));
            }
        }

        if (position.Column < 0 || position.Column >= word.Count || !word[position.Column])
        {
            return;
        }

        int start = position.Column;
        while (start > 0 && word[start - 1])
        {
            start--;
        }

        int end = position.Column;
        while (end < word.Count - 1 && word[end + 1])
        {
            end++;
        }

        _selectionAnchor = new TerminalPosition(position.Row, start);
        _selectionCaret = new TerminalPosition(position.Row, end);
        InvalidatePaint(force: true);
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or '~' or '@' or ':';

    private void SelectAll()
    {
        var emulator = _bridge.Emulator;
        int rows;
        int columns;
        lock (emulator.SyncRoot)
        {
            rows = emulator.Rows;
            columns = emulator.Columns;
        }

        _selectionAnchor = new TerminalPosition(0, 0);
        _selectionCaret = new TerminalPosition(Math.Max(0, rows - 1), Math.Max(0, columns - 1));
        InvalidatePaint(force: true);
    }

    private void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionCaret is null)
        {
            return;
        }

        _selectionAnchor = null;
        _selectionCaret = null;
    }

    private void ScrollViewportByPage(bool back)
    {
        var emulator = _bridge.Emulator;
        int rows;
        lock (emulator.SyncRoot)
        {
            rows = Math.Max(1, emulator.Rows - 1);
            ScrollHistory(back ? rows : -rows);
        }

        InvalidatePaint(force: true);
    }

    private void ScrollHistory(int lines)
    {
        var emulator = _bridge.Emulator;
        lock (emulator.SyncRoot)
        {
            var before = emulator.ViewportOffset;
            emulator.ScrollViewport(lines);
            var shift = emulator.ViewportOffset - before;
            if (_selectionAnchor is { } anchor) { _selectionAnchor = anchor with { Row = anchor.Row + shift }; }
            if (_selectionCaret is { } caret) { _selectionCaret = caret with { Row = caret.Row + shift }; }
        }
        InvalidatePaint(force: true);
    }

    // --- clipboard -------------------------------------------------------------

    private bool CopySelection()
    {
        TerminalSelectionRange range = TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret);
        if (!range.HasSelection)
        {
            return false;
        }

        var emulator = _bridge.Emulator;
        string text;
        lock (emulator.SyncRoot)
        {
            text = emulator.GetSelectionText(new TerminalSelection(range.First, range.Last));
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        return true;
    }

    private async void PasteFromClipboard()
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (view is null || !view.Contains(StandardDataFormats.Text))
            {
                return;
            }

            string text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Terminals expect CR line endings; pasting LF only confuses readline.
            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
            _bridge.SendPaste(normalized);
        }
        catch (Exception)
        {
            // The clipboard is a shared resource; a failed read must not crash the app.
        }
    }

    private void ShowContextMenu(Point position)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.copy", "Copy"), "\uE8C8", () => CopySelection(), HasSelection));
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.paste", "Paste"), "\uE77F", PasteFromClipboard, true));
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.selectAll", "Select all"), "\uE8B3", SelectAll, true));
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.clear", "Clear"), "\uE894", ClearScreen, true));
        flyout.ShowAt(this, new FlyoutShowOptions { Position = position });
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Action action, bool enabled)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = enabled,
        };

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Clears the local screen (the remote shell redraws its prompt on the next command).</summary>
    private void ClearScreen()
    {
        var emulator = _bridge.Emulator;
        lock (emulator.SyncRoot)
        {
            emulator.Reset();
        }

        ClearSelection();
        InvalidatePaint(force: true);
    }

    // --- font zoom -------------------------------------------------------------

    private void ZoomFont(int delta)
    {
        AppSettings current = _settings.Current;
        double next = Math.Clamp(current.TerminalFontSize + delta, MinFontSize, MaxFontSize);
        if (Math.Abs(next - current.TerminalFontSize) < 0.01)
        {
            return;
        }

        // Persisted through the store, so the zoom survives restarts and every open
        // terminal picks it up from its own Changed handler.
        _settings.Update(settings => settings with { TerminalFontSize = next });
    }

    private void ResetFontSize()
    {
        if (Math.Abs(_settings.Current.TerminalFontSize - DefaultFontSize) < 0.01)
        {
            return;
        }

        _settings.Update(settings => settings with { TerminalFontSize = DefaultFontSize });
    }
}