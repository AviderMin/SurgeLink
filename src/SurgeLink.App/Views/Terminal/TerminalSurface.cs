using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Terminal;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// The terminal control for one session: it owns the cell grid visuals and every
/// keyboard / pointer interaction. There is no XAML and no per-cell element: the
/// visual tree is three stacked canvases (backgrounds, text runs, cursor) built in
/// C#, and only the rows that actually changed are touched on each repaint.
/// </summary>
/// <remarks>
/// <para><b>Repaint model.</b> <see cref="TerminalBridge.Invalidated"/> already runs on
/// the UI thread and is coalesced per frame, so the control queues one dispatcher
/// callback per burst. The callback copies the visible grid into per-row snapshots
/// while holding <see cref="ITerminalEmulator.SyncRoot"/> (the feed thread takes the
/// same lock), then diffs each row against the previous snapshot outside the lock.
/// Diffing outside the lock keeps a slow paint from stalling the network. A row that
/// did not change costs a comparison and nothing else.</para>
/// <para><b>Why this is not a Reactor element.</b> Reactor owns the composition, but a
/// grid of thousands of cells cannot be described as immutable elements per frame
/// without allocating; the surface is hosted through
/// <c>Microsoft.UI.Reactor.Hosting.XamlHostElement</c> (see <see cref="TerminalView"/>),
/// created once per mount and mutated in place.</para>
/// </remarks>
internal sealed partial class TerminalSurface : ContentControl
{
    /// <summary>Automation id exposed to UI Automation and the team's UIA dumps.</summary>
    internal const string AutomationId = "TerminalSurface";

    /// <summary>Blink interval of the cursor in milliseconds (the Windows Terminal value).</summary>
    private const double CursorBlinkMilliseconds = 530;

    /// <summary>Wheel notches are worth this many scrollback lines.</summary>
    private const int LinesPerWheelNotch = 3;

    private readonly TerminalBridge _bridge;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger? _logger;
    private const double ContentInset = 12;
    private const double ScrollBarWidth = 16;
    private readonly Microsoft.UI.Xaml.Controls.Primitives.ScrollBar _scrollBar;
    private bool _updatingScrollBar;
    private readonly Grid _root;
    private readonly Canvas _backgroundLayer;
    private readonly Canvas _textLayer;
    private readonly Canvas _cursorLayer;
    private readonly Rectangle _cursorRect;
    private readonly TextBlock _cursorGlyph;
    private readonly DispatcherQueueTimer? _blinkTimer;
    private readonly StringBuilder _runBuilder = new(64);

    private string _fontFamilyName = string.Empty;
    private double _fontSize;
    private TerminalMetrics? _metrics;
    private TerminalStyleResolver? _resolver;

    private TerminalRowVisuals[] _rowVisuals = Array.Empty<TerminalRowVisuals>();
    private TerminalRowSnapshot[] _paintedRows = Array.Empty<TerminalRowSnapshot>();
    private TerminalRowSnapshot[] _scratchRows = Array.Empty<TerminalRowSnapshot>();

    private int _requestedColumns = -1;
    private int _requestedRows = -1;
    private int _paintedColumns;
    private int _paintedRowCount;
    private long _paintedVersion = -1;
    private bool _forcePaint = true;
    private bool _allRowsDirty = true;
    private bool _paintQueued;
    private bool _loaded;
    private bool _hasFocus;
    private bool _blinkOn = true;
    private string _loggedLayout = string.Empty;

    private TerminalPosition? _selectionAnchor;
    private TerminalPosition? _selectionCaret;
    private bool _selecting;

    /// <summary>Creates the surface for a live session.</summary>
    /// <param name="bridge">Bridge whose emulator is rendered and whose remote receives input.</param>
    /// <param name="settings">Settings store used for the font and colour scheme.</param>
    /// <param name="logger">Optional diagnostic log; metrics and the painted element count land here.</param>
    public TerminalSurface(TerminalBridge bridge, IAppSettingsStore settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(settings);

        _bridge = bridge;
        _settings = settings;
        _logger = logger;

        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        AutomationProperties.SetAutomationId(this, AutomationId);

        _backgroundLayer = CreateLayer();
        _textLayer = CreateLayer();
        _cursorLayer = CreateLayer();
        _cursorRect = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _cursorGlyph = new TextBlock
        {
            IsHitTestVisible = false,
            IsTextScaleFactorEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            Visibility = Visibility.Collapsed,
        };
        _cursorLayer.Children.Add(_cursorRect);
        _cursorLayer.Children.Add(_cursorGlyph);

        _root = new Grid();
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ScrollBarWidth) });
        foreach (var layer in new[] { _backgroundLayer, _textLayer, _cursorLayer })
        {
            layer.Margin = new Thickness(ContentInset);
        }
        _scrollBar = new Microsoft.UI.Xaml.Controls.Primitives.ScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 1,
            IsTabStop = false,
            Width = ScrollBarWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Standalone ScrollBar has no ScrollViewer to activate its indicator.
            IndicatorMode = Microsoft.UI.Xaml.Controls.Primitives.ScrollingIndicatorMode.MouseIndicator,
            Opacity = 0,
        };
        HookScrollBarHover();
        Grid.SetColumn(_scrollBar, 1);
        _scrollBar.ValueChanged += (_, args) =>
        {
            if (_updatingScrollBar) { return; }
            var emulator = _bridge.Emulator;
            lock (emulator.SyncRoot)
            {
                ScrollHistory(emulator.ScrollbackLength - (int)Math.Round(args.NewValue) - emulator.ViewportOffset);
            }
        };
        _root.Children.Add(_scrollBar);
        _root.Children.Add(_backgroundLayer);
        _root.Children.Add(_textLayer);
        _root.Children.Add(_cursorLayer);
        Content = _root;

        ApplySettings(settings.Current, initial: true);
        HookInput();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        DispatcherQueueTimer? timer = DispatcherQueue?.CreateTimer();
        if (timer is not null)
        {
            timer.Interval = TimeSpan.FromMilliseconds(CursorBlinkMilliseconds);
            timer.Tick += OnBlinkTick;
            _blinkTimer = timer;
        }
    }

    /// <summary>Re-applies settings and repaints; called by the Reactor host on every render.</summary>
    internal void Refresh()
    {
        ApplySettings(_settings.Current, initial: false);
        InvalidatePaint(force: true);
    }

    /// <summary>
    /// Visible screen text for the automation peer. Read under the emulator lock
    /// because UI Automation calls this from its own thread while output is flowing.
    /// </summary>
    internal string GetAccessibleText()
    {
        var emulator = _bridge.Emulator;
        try
        {
            lock (emulator.SyncRoot)
            {
                return emulator.GetVisibleText();
            }
        }
        catch (NotImplementedException)
        {
            // A protocol may hand us a not-yet-implemented emulator; never throw into UIA.
            return string.Empty;
        }
    }

    /// <summary>Subscribes every keyboard, pointer and focus handler exactly once.</summary>
    private void HookInput()
    {
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerCanceled += OnPointerCaptureLost;
        PointerWheelChanged += OnPointerWheelChanged;
        DoubleTapped += OnDoubleTapped;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
    }

    private static Canvas CreateLayer() => new() { IsHitTestVisible = false };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            _loaded = true;
            _bridge.Invalidated += OnBridgeInvalidated;
            _settings.Changed += OnSettingsChanged;
        }

        UpdateGridSize();
        InvalidatePaint(force: true);
        _logger?.LogInformation(
            "Terminal surface loaded: size={Width}x{Height}, grid={Columns}x{Rows}",
            Math.Round(ActualWidth, 1),
            Math.Round(ActualHeight, 1),
            _requestedColumns,
            _requestedRows);

        // A terminal is only useful once it has focus: take it as soon as the view appears.
        DispatcherQueue?.TryEnqueue(() => Focus(FocusState.Programmatic));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _pointerOverTerminal = false;
        _scrollBarInteracting = false;
        UpdateScrollBarVisibility();
        _selecting = false;
        ReleasePointerCaptures();
        _paintQueued = false;
        _bridge.Invalidated -= OnBridgeInvalidated;
        _settings.Changed -= OnSettingsChanged;
        StopBlink();
    }

    private void OnBridgeInvalidated(object? sender, EventArgs e) => InvalidatePaint(force: false);

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings(_settings.Current, initial: false);

    private void InvalidatePaint(bool force)
    {
        if (force)
        {
            _forcePaint = true;
        }

        if (!_loaded || _paintQueued)
        {
            return;
        }

        _paintQueued = true;
        var queue = DispatcherQueue;
        if (queue is null)
        {
            Paint();
            return;
        }

        if (!queue.TryEnqueue(Paint))
        {
            _paintQueued = false;
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;
        _blinkOn = true;
        StartBlink();
        InvalidatePaint(force: true);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = false;
        StopBlink();
        _blinkOn = true;
        InvalidatePaint(force: true);
    }

    private void StartBlink()
    {
        if (_blinkTimer is not null && !_blinkTimer.IsRunning)
        {
            _blinkTimer.Start();
        }
    }

    private void StopBlink()
    {
        if (_blinkTimer is not null && _blinkTimer.IsRunning)
        {
            _blinkTimer.Stop();
        }
    }

    private void OnBlinkTick(DispatcherQueueTimer sender, object args)
    {
        _blinkOn = !_blinkOn;

        // Blinking only toggles the cursor layer: no cell repaint is involved.
        _cursorLayer.Opacity = _blinkOn ? 1 : 0;
    }
}
