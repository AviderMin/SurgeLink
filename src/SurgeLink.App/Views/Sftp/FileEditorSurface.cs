using System.Globalization;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using Windows.Foundation;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// Retains the native text buffer/undo/selection across shell renders. The gutter
/// owns only viewport-sized visuals and never lays out a document-sized TextBlock.
/// </summary>
internal sealed class FileEditorSurface : Grid
{
    private readonly Canvas _gutter = new() { IsHitTestVisible = false };
    private readonly List<TextBlock> _labels = new();
    private readonly Dictionary<int, Rect> _lineRects = new();
    private readonly ColumnDefinition _gutterColumn = new();
    private readonly double _lineHeight;
    private readonly EditorLineIndex _lines = new(string.Empty);
    private string _savedText = string.Empty;
    private string _editorText = string.Empty;
    private int _documentRevision = -1;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statisticsTimer;
    private ScrollViewer? _scroll;
    private bool _settingText;
    private bool _gutterQueued;

    public TextBox Editor { get; } = new()
    {
        AcceptsReturn = true,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        FontFamily = AppTheme.Monospace,
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    public Action? TextEdited { get; set; }
    public Action<bool, int, int>? StatisticsChanged { get; set; }
    public string ReadDocument() => EditorTextBuffer.ToDocumentText(Editor.Text);
    public bool HasUnsavedChanges() => !EditorTextBuffer.MatchesDocument(Editor.Text, _savedText);
    public void SetBaseline(string text) => _savedText = text;

    public FileEditorSurface()
    {
        var metrics = new TextBlock { Text = "1", FontFamily = Editor.FontFamily, FontSize = Editor.FontSize };
        metrics.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _lineHeight = metrics.DesiredSize.Height;
        ColumnDefinitions.Add(_gutterColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(_gutter);
        SetColumn(Editor, 1);
        Children.Add(Editor);
        ScrollViewer.SetVerticalScrollBarVisibility(Editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(Editor, ScrollBarVisibility.Auto);
        _statisticsTimer = DispatcherQueue.CreateTimer();
        _statisticsTimer.IsRepeating = false;
        _statisticsTimer.Interval = TimeSpan.FromMilliseconds(150);
        _statisticsTimer.Tick += (_, _) => RefreshStatistics();
        Editor.TextChanged += (_, _) =>
        {
            if (_settingText) { return; }
            _statisticsTimer.Stop();
            _statisticsTimer.Start();
            TextEdited?.Invoke();
        };
        Loaded += (_, _) =>
        {
            _statisticsTimer.Start();
            var scroll = FindScrollViewer(Editor);
            if (!ReferenceEquals(_scroll, scroll))
            {
                if (_scroll is not null) { _scroll.ViewChanged -= OnViewChanged; }
                _scroll = scroll;
                if (_scroll is not null) { _scroll.ViewChanged += OnViewChanged; }
            }
            QueueGutter();
        };
        Unloaded += (_, _) =>
        {
            if (_scroll is not null) { _scroll.ViewChanged -= OnViewChanged; }
            _scroll = null;
            _statisticsTimer.Stop();
            CompositionTarget.Rendering -= OnGutterRendering;
            _gutterQueued = false;
        };
        SizeChanged += (_, _) => QueueGutter();
        Editor.SizeChanged += (_, _) => { _lineRects.Clear(); QueueGutter(); };
        ActualThemeChanged += (_, _) => QueueGutter();
    }

    public void Update(string text, int documentRevision, bool wrap, string automationName)
    {
        AutomationProperties.SetName(Editor, automationName);
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (Editor.TextWrapping != wrapping)
        {
            Editor.TextWrapping = wrapping;
            _lineRects.Clear();
            QueueGutter();
        }
        // A user edit already lives in the native buffer. Only load/reload writes
        // Text, so toolbar/find/status updates cannot reset undo or scroll/caret.
        if (_documentRevision != documentRevision)
        {
            _documentRevision = documentRevision;
            _savedText = text;
            _statisticsTimer.Stop();
            _settingText = true;
            try
            {
                Editor.Text = EditorTextBuffer.ToEditorText(text);
            }
            finally { _settingText = false; }
            _statisticsTimer.Start();
        }
    }

    private void RefreshStatistics()
    {
        var buffer = Editor.Text;
        _editorText = buffer;
        _lines.Update(buffer);
        _lineRects.Clear();
        QueueGutter();
        StatisticsChanged?.Invoke(!EditorTextBuffer.MatchesDocument(buffer, _savedText), _lines.Count,
            EditorTextBuffer.ToDocumentOffset(buffer, buffer.Length));
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => QueueGutter();

    private void QueueGutter()
    {
        if (_gutterQueued) { return; }
        _gutterQueued = true;
        CompositionTarget.Rendering += OnGutterRendering;
    }

    private void OnGutterRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnGutterRendering;
        _gutterQueued = false;
        if (IsLoaded && !_statisticsTimer.IsRunning) { PaintGutter(); }
    }

    private void PaintGutter()
    {
        var width = Math.Max(48, _lines.Count.ToString(CultureInfo.InvariantCulture).Length * 9 + 16);
        if (_gutterColumn.Width.Value != width) { _gutterColumn.Width = new GridLength(width); }
        var top = _scroll?.TransformToVisual(Editor).TransformPoint(new Point()).Y ?? 0;
        var height = _scroll?.ViewportHeight ?? Editor.ActualHeight;
        _gutter.Clip = new RectangleGeometry { Rect = new Rect(0, top, width, height) };
        var verticalOffset = _scroll?.VerticalOffset ?? 0;
        var line = _lines.FirstVisibleLine(offset => LineRect(offset).Bottom, verticalOffset);
        var foreground = ThemeRef.Resolve("TextFillColorSecondaryBrush", ActualTheme == ElementTheme.Dark) as Brush
            ?? Editor.Foreground;
        var used = 0;
        for (; line < _lines.Count; line++)
        {
            var rect = LineRect(_lines[line]);
            var y = top + Editor.Padding.Top + rect.Top - verticalOffset;
            if (y >= top + height) { break; }
            // Geometry is unavailable until the native text layout has run.
            if (rect.Height <= 0) { break; }
            if (used == _labels.Count)
            {
                var label = new TextBlock
                {
                    FontFamily = AppTheme.Monospace,
                    FontSize = Editor.FontSize,
                    TextAlignment = TextAlignment.Right,
                    IsHitTestVisible = false,
                };
                _labels.Add(label);
                _gutter.Children.Add(label);
            }
            var block = _labels[used++];
            var number = (line + 1).ToString(CultureInfo.InvariantCulture);
            if (block.Text != number) { block.Text = number; }
            block.Foreground = foreground;
            block.Width = width - 8;
            block.Visibility = Visibility.Visible;
            Canvas.SetTop(block, y);
        }
        for (; used < _labels.Count; used++) { _labels[used].Visibility = Visibility.Collapsed; }
    }

    private Rect LineRect(int offset)
    {
        // WinUI requires a real character index, so neither an empty buffer nor
        // the final empty line may be queried at Text.Length.
        if (_editorText.Length == 0)
        {
            return new Rect(0, 0, 0, _lineHeight);
        }
        if (offset == _editorText.Length)
        {
            var newline = offset - 1;
            if (newline > 0 && _editorText[newline] == '\n' && _editorText[newline - 1] == '\r') { newline--; }
            var previous = Editor.GetRectFromCharacterIndex(newline, false);
            return new Rect(0, previous.Top + _lineHeight, 0, _lineHeight);
        }
        if (_lineRects.TryGetValue(offset, out var cached)) { return cached; }
        var rect = Editor.GetRectFromCharacterIndex(offset, false);
        // Keep a bounded cache of document-space geometry. Most binary-search
        // probes repeat while scrolling; they should not cross the WinRT boundary.
        if (rect.Height > 0)
        {
            if (_lineRects.Count >= 4096) { _lineRects.Clear(); }
            _lineRects[offset] = rect;
        }
        return rect;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer) { return viewer; }
            if (FindScrollViewer(child) is { } nested) { return nested; }
        }
        return null;
    }
}
