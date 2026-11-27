using Microsoft.Extensions.Logging;
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
    // Keep in step with AppSettingsStore.Normalize so zooming at a limit is a no-op.
    private const double MinFontSize = 9;
    private const double MaxFontSize = 28;
    private const double DefaultFontSize = 13;

    private bool HasSelection => TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret, _allowSingleCellSelection).HasSelection;
    private TerminalMouseButton _remoteMouseButton = TerminalMouseButton.None;
    private (int Row, int Column, TerminalMouseButton Button)? _lastMouseMove;
    private bool _allowSingleCellSelection;
    private int _wheelRemainder;

    private bool ReportMouse(Point point, TerminalMouseButton button, TerminalMouseAction action, bool completePress = false)
    {
        var cell = CellFromPoint(point);
        return _bridge.SendMouse(cell.Column, cell.Row, button, action,
            IsKeyDown(VirtualKey.Control), IsKeyDown(VirtualKey.Menu), !completePress && IsKeyDown(VirtualKey.Shift));
    }

    private bool IsScrollBarEvent(PointerRoutedEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != this;
             source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source))
        {
            if (source is ScrollBar) { return true; }
        }
        return false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsScrollBarEvent(e) || IsSearchBarEvent(e.OriginalSource)) { return; }
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);

        var button = point.Properties.PointerUpdateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed => TerminalMouseButton.Left,
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed => TerminalMouseButton.Middle,
            Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed => TerminalMouseButton.Right,
            _ => TerminalMouseButton.None,
        };
        if (button != TerminalMouseButton.None && ReportMouse(point.Position, button, TerminalMouseAction.Press))
        {
            _remoteMouseButton = button;
            _lastMouseMove = null;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

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
        if (IsKeyDown(VirtualKey.Control)
            && _bridge.Emulator.GetHyperlinkAt(cell.Row, cell.Column) is { } link
            && Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
            e.Handled = true;
            return;
        }
        if (IsKeyDown(VirtualKey.Shift) && HasSelection)
        {
            // Shift+click extends the existing selection instead of restarting it.
            _selectionCaret = cell;
        }
        else
        {
            _selectionAnchor = cell;
            _selectionCaret = cell;
            _allowSingleCellSelection = false;
        }

        _selecting = true;
        CapturePointer(e.Pointer);
        InvalidatePaint(force: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsScrollBarEvent(e) || IsSearchBarEvent(e.OriginalSource)) { return; }
        if (!_selecting)
        {
            bool mouseTracking;
            lock (_bridge.Emulator.SyncRoot)
            {
                mouseTracking = _bridge.Emulator.Modes.MouseTracking;
            }
            if (mouseTracking)
            {
                var point = e.GetCurrentPoint(this);
                var button = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left
                    : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                    : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right : TerminalMouseButton.None;
                var mouseCell = CellFromPoint(point.Position);
                var move = (mouseCell.Row, mouseCell.Column, button);
                if (_lastMouseMove != move && ReportMouse(point.Position, button, TerminalMouseAction.Move))
                {
                    _lastMouseMove = move;
                    e.Handled = true;
                    return;
                }
            }
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
        if (_remoteMouseButton == TerminalMouseButton.None && IsSearchBarEvent(e.OriginalSource)) { return; }
        if (_remoteMouseButton != TerminalMouseButton.None)
        {
            var point = e.GetCurrentPoint(this);
            var released = point.Properties.PointerUpdateKind switch
            {
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => TerminalMouseButton.Left,
                Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => TerminalMouseButton.Middle,
                Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => TerminalMouseButton.Right,
                _ => _remoteMouseButton,
            };
            ReportMouse(point.Position, released, TerminalMouseAction.Release, completePress: true);
            var remaining = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left
                : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right : TerminalMouseButton.None;
            _remoteMouseButton = remaining;
            _lastMouseMove = null;
            if (remaining == TerminalMouseButton.None)
            {
                ReleasePointerCapture(e.Pointer);
            }
            e.Handled = true;
            return;
        }
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        _remoteMouseButton = TerminalMouseButton.None;
        _lastMouseMove = null;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _selecting = false;
        _remoteMouseButton = TerminalMouseButton.None;
        _lastMouseMove = null;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsSearchBarEvent(e.OriginalSource)) { return; }
        lock (_bridge.Emulator.SyncRoot)
        {
            if (_bridge.Emulator.Modes.MouseTracking && !IsKeyDown(VirtualKey.Shift)) { e.Handled = true; return; }
        }
        SelectWordAt(CellFromPoint(e.GetPosition(this)));
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsScrollBarEvent(e) || IsSearchBarEvent(e.OriginalSource)) { return; }
        var point = e.GetCurrentPoint(this);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var wheel = point.Properties.IsHorizontalMouseWheel
            ? (delta > 0 ? TerminalMouseButton.WheelRight : TerminalMouseButton.WheelLeft)
            : (delta > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown);
        if (ReportMouse(point.Position, wheel, TerminalMouseAction.Wheel))
        {
            _wheelRemainder = 0;
            for (var notch = 1; notch < Math.Abs(delta / 120); notch++) { ReportMouse(point.Position, wheel, TerminalMouseAction.Wheel); }
            e.Handled = true;
            return;
        }

        if (point.Properties.IsHorizontalMouseWheel)
        {
            e.Handled = true;
            return;
        }

        if (IsKeyDown(VirtualKey.Control))
        {
            _wheelRemainder = 0;
            ZoomFont(delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        if ((_wheelRemainder > 0 && delta < 0) || (_wheelRemainder < 0 && delta > 0))
        {
            _wheelRemainder = 0;
        }

        _wheelRemainder += delta;
        int notches = _wheelRemainder / 120;
        _wheelRemainder %= 120;
        int lines = notches * LinesPerWheelNotch;
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
        (int StartColumn, int EndColumn)? bounds;
        lock (emulator.SyncRoot)
        {
            bounds = TerminalWordSelection.FindWordBounds(emulator, position.Row, position.Column);
        }

        if (bounds is not { } word)
        {
            return;
        }

        _selectionAnchor = new TerminalPosition(position.Row, word.StartColumn);
        _selectionCaret = new TerminalPosition(position.Row, word.EndColumn);
        _allowSingleCellSelection = true;
        InvalidatePaint(force: true);
    }

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
        _allowSingleCellSelection = true;
        InvalidatePaint(force: true);
    }

    private void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionCaret is null && !_allowSingleCellSelection)
        {
            return;
        }

        _selectionAnchor = null;
        _selectionCaret = null;
        _allowSingleCellSelection = false;
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
        TerminalSelectionRange range = TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret, _allowSingleCellSelection);
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
        try { Clipboard.SetContent(package); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger?.LogDebug(ex, "Terminal clipboard write failed.");
            return false;
        }
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
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.find", "Find…"), "\uE721", () => OpenSearch(), true));
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.copy", "Copy"), "\uE8C8", () => CopySelection(), HasSelection));
        flyout.Items.Add(CreateMenuItem(Str.T("terminal.paste", "Paste"), "\uE77F", PasteFromClipboard, true));
        // Mixing ToggleMenuFlyoutItem with icon items triggers WinUI's CheckAndIconPlaceholder
        // state, adding an empty checkmark gutter to the left of every item's icon.
        flyout.Items.Add(CreateMenuItem(
            Str.T("terminal.remoteClipboard", "Allow this shell to write to the clipboard"),
            _bridge.AllowRemoteClipboard ? "\uE73A" : "\uE739",
            () => _bridge.AllowRemoteClipboard = !_bridge.AllowRemoteClipboard,
            !_bridge.IsClosed));
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
            // Keep modes, SGR and the alternate screen intact: a remote application
            // such as less or vim still believes it owns the alternate screen, so a
            // full Reset would leave the two sides out of sync.
            emulator.ClearDisplay();
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
        TryUpdateFontSize(next);
    }

    private void ResetFontSize()
    {
        if (Math.Abs(_settings.Current.TerminalFontSize - DefaultFontSize) < 0.01)
        {
            return;
        }

        TryUpdateFontSize(DefaultFontSize);
    }

    private void TryUpdateFontSize(double size)
    {
        try
        {
            _settings.UpdateTerminalFontSize(size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only settings file must not tear down the process
            // from a keyboard/wheel handler; the zoom is simply not persisted.
            _logger?.LogWarning(ex, "Could not persist the terminal font size.");
        }
    }
}
