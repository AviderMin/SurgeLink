using Microsoft.Extensions.Logging;
using SurgeLink.App.Services;
using SurgeLink.Core.Terminal;
using SurgeLink.Terminal;

namespace SurgeLink.App.Sessions;

/// <summary>
/// Glue between a protocol terminal stream and the emulator: feeds bytes into
/// the screen buffer, coalesces repaint notifications onto the UI thread and
/// turns UI input back into byte sequences.
/// </summary>
public sealed partial class TerminalBridge : IAsyncDisposable, ITerminalAutomation
{
    /// <summary>Stable, unique identity for native surface reconciliation across shell replacement.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    private readonly IRemoteTerminal _remote;
    private readonly Action<ReadOnlySpan<byte>> _recordOutput;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IAppSettingsStore? _settings;
    private readonly string _terminalType;
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private long _resizeRequest;
    private (int Columns, int Rows) _remoteSize;
    private int _repaintScheduled;
    private int _activityScheduled;
    private int _closed;
    private volatile bool _allowRemoteClipboard;

    /// <summary>Wraps a remote terminal; call <see cref="StartAsync"/> to open the shell channel.</summary>
    public TerminalBridge(IRemoteTerminal remote, IUiDispatcher dispatcher, ILogger logger, int columns = 120, int rows = 32,
        IAppSettingsStore? settings = null, string? terminalType = null, TerminalEmulator? existingEmulator = null,
        string? banner = null)
    {
        _remote = remote;
        _recordOutput = _output.Append;
        _dispatcher = dispatcher;
        _logger = logger;
        _settings = settings;
        _terminalType = string.IsNullOrWhiteSpace(terminalType) ? "xterm-256color" : terminalType.Trim();
        if (existingEmulator is not null)
        {
            Emulator = existingEmulator;
            lock (Emulator.SyncRoot)
            {
                _remoteSize = (Emulator.Columns, Emulator.Rows);
                Emulator.ResetForNewShell();
                if (!string.IsNullOrEmpty(banner))
                {
                    var text = $"\r\n\u001b[2m{banner}\u001b[0m\r\n";
                    Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(text));
                }
            }
        }
        else
        {
            Emulator = new TerminalEmulator(columns, rows, settings?.Current.TerminalScrollbackLines ?? 10000)
            {
                ReflowOnResize = true,
            };
            _remoteSize = (columns, rows);
        }
        Emulator.TerminalType = _terminalType;
        ApplyTerminalTheme();
        if (settings is not null) settings.Changed += OnSettingsChanged;
        Emulator.ResponseRequested += OnResponseRequested;
        if (_remote is IBorrowedTerminalOutput borrowed) { borrowed.BorrowedOutputReceived += OnOutputReceived; }
        else { _remote.OutputReceived += OnOutputReceived; }
        _remote.Closed += OnRemoteClosed;
    }

    /// <summary>The screen buffer the view renders.</summary>
    public TerminalEmulator Emulator { get; }

    internal bool AllowRemoteClipboard
    {
        get => _allowRemoteClipboard;
        set => _allowRemoteClipboard = value;
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        if (IsClosed || _settings is null) return;
        Emulator.SetScrollbackLimit(_settings.Current.TerminalScrollbackLines);
        ApplyTerminalTheme();
        RaiseInvalidated();
    }

    private void ApplyTerminalTheme()
    {
        var name = _settings?.Current.TerminalPalette;
        var palette = TerminalThemes.All.FirstOrDefault(theme =>
            string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)).Palette;
        Emulator.SetBasePalette(palette ?? TerminalThemes.Campbell);
    }

    /// <summary>Raised on the UI thread after new output was parsed; coalesced per frame.</summary>
    public event EventHandler? Invalidated;

    /// <summary>
    /// Raised on the UI thread when remote terminal output arrives. Coalesced per
    /// dispatcher turn so a fast stream does not queue one notification per chunk.
    /// </summary>
    public event EventHandler? Activity;

    /// <summary>Raised on the UI thread when the remote shell exits.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised before a human input is queued, so external control can be paused immediately.</summary>
    public event EventHandler? UserInput;

    /// <summary>True once the remote side closed the channel.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Opens the shell channel using the emulator's current size.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        int columns;
        int rows;
        lock (Emulator.SyncRoot)
        {
            columns = Emulator.Columns;
            rows = Emulator.Rows;
        }

        return _remote.StartAsync(
            new TerminalStartOptions { Columns = columns, Rows = rows, TerminalType = _terminalType },
            cancellationToken);
    }

    /// <summary>Sends text typed or pasted by the user.</summary>
    public void SendText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            MarkUserInput();
            lock (Emulator.SyncRoot) Send(TerminalInputEncoder.EncodeText(text, Emulator.Modes));
        }
    }

    /// <summary>Sends a paste, honouring bracketed paste mode.</summary>
    public void SendPaste(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            MarkUserInput();
            lock (Emulator.SyncRoot) Send(TerminalInputEncoder.EncodePaste(text, Emulator.Modes));
        }
    }

    /// <summary>
    /// Encodes and sends a key press. Returns <c>false</c> when the key has no
    /// terminal encoding, so the caller can let the keyboard layout deliver the
    /// character through CharacterReceived instead of swallowing it.
    /// </summary>
    public bool SendKey(int virtualKey, bool control = false, bool alt = false, bool shift = false)
    {
        byte[]? bytes;
        lock (Emulator.SyncRoot) bytes = TerminalInputEncoder.EncodeKey(virtualKey, control, alt, shift, Emulator.Modes);
        if (bytes is not { Length: > 0 })
        {
            return false;
        }

        MarkUserInput();
        Send(bytes);
        return true;
    }

    /// <summary>Reports a human mouse event, sharing serialization and Agent takeover with keyboard input.</summary>
    public bool SendMouse(int column, int row, TerminalMouseButton button, TerminalMouseAction action,
        bool control = false, bool alt = false, bool shift = false)
    {
        byte[]? bytes;
        lock (Emulator.SyncRoot) { bytes = TerminalMouseEncoder.Encode(column, row, button, action, Emulator.Modes, control, alt, shift); }
        if (bytes is not { Length: > 0 } || IsClosed) { return false; }
        MarkUserInput();
        Send(bytes);
        return true;
    }

    /// <summary>
    /// Resizes the emulator and tells the server about the new PTY size.
    /// Remote resizes are serialized and only the newest requested size is sent:
    /// overlapping SizeChanged events must not let an older request reach the PTY
    /// after a newer one, or the remote dimensions diverge from the local screen.
    /// </summary>
    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        long request;
        lock (Emulator.SyncRoot)
        {
            request = ++_resizeRequest;
            if (Emulator.Columns != columns || Emulator.Rows != rows)
            {
                Emulator.Resize(columns, rows);
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _resizeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A later request superseded this one while it waited; that request
            // sends the final size, so this one must not overwrite it.
            if (Interlocked.Read(ref _resizeRequest) != request)
            {
                return;
            }

            // A duplicate layout event must still carry a pending or failed
            // remote resize. Local dimensions alone do not confirm delivery.
            if (_remoteSize != (columns, rows))
            {
                await _remote.ResizeAsync(columns, rows, token).ConfigureAwait(false);
                _remoteSize = (columns, rows);
            }
        }
        finally
        {
            _resizeGate.Release();
        }

        RaiseInvalidated();
    }

    private void OnResponseRequested(string response) => Send(System.Text.Encoding.UTF8.GetBytes(response));

    private async void Send(byte[] bytes)
    {
        try
        {
            // The lifetime token lets DisposeAsync cancel a blocked write without
            // waiting for the remote transport to release it first.
            await _inputGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { if (!IsClosed) await _remote.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false); }
            finally { _inputGate.Release(); }
        }
        catch (Exception ex)
        {
            // The write path wraps protocol failures in ProtocolException; an
            // async void handler must never let any of them escape unobserved.
            // A failure while the channel still claims to be open is actionable
            // diagnostics and must reach the normal Information-level log file.
            if (IsClosed)
            {
                _logger.LogDebug(ex, "Terminal write failed after the channel closed.");
            }
            else
            {
                _logger.LogWarning(ex, "Terminal write failed while the session is open.");
            }
        }
    }

    private void OnOutputReceived(object? sender, TerminalOutputEventArgs e)
        => OnOutputReceived(e.Data.Span);

    private void OnOutputReceived(ReadOnlySpan<byte> data)
    {
        Emulator.Feed(data, _recordOutput);

        ScheduleRepaint();
        ScheduleActivity();
    }

    private void OnRemoteClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _dispatcher.Post(() => Closed?.Invoke(this, EventArgs.Empty));
        }
    }

    private void ScheduleRepaint()
    {
        if (Interlocked.Exchange(ref _repaintScheduled, 1) != 0)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            Interlocked.Exchange(ref _repaintScheduled, 0);
            Invalidated?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ScheduleActivity()
    {
        if (Interlocked.Exchange(ref _activityScheduled, 1) != 0)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            Interlocked.Exchange(ref _activityScheduled, 0);
            Activity?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RaiseInvalidated()
    {
        if (_dispatcher.IsOnUiThread)
        {
            Invalidated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.Post(() => Invalidated?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Share one completion among every close caller. Do not wait for
        // _inputGate here: a write may be blocked inside the transport and only
        // complete after DisposeAsync releases that transport.
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed by an earlier caller */ }

        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        Emulator.ResponseRequested -= OnResponseRequested;
        if (_remote is IBorrowedTerminalOutput borrowed) { borrowed.BorrowedOutputReceived -= OnOutputReceived; }
        else { _remote.OutputReceived -= OnOutputReceived; }
        _remote.Closed -= OnRemoteClosed;
        await _remote.DisposeAsync().ConfigureAwait(false);
    }
}
