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
    private readonly IRemoteTerminal _remote;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IAppSettingsStore? _settings;
    private int _repaintScheduled;
    private int _closed;

    /// <summary>Wraps a remote terminal; call <see cref="StartAsync"/> to open the shell channel.</summary>
    public TerminalBridge(IRemoteTerminal remote, IUiDispatcher dispatcher, ILogger logger, int columns = 120, int rows = 32,
        IAppSettingsStore? settings = null)
    {
        _remote = remote;
        _dispatcher = dispatcher;
        _logger = logger;
        _settings = settings;
        Emulator = new TerminalEmulator(columns, rows, settings?.Current.TerminalScrollbackLines ?? 10000);
        if (settings is not null) settings.Changed += OnSettingsChanged;
        Emulator.ResponseRequested += OnResponseRequested;
        _remote.OutputReceived += OnOutputReceived;
        _remote.Closed += OnRemoteClosed;
    }

    /// <summary>The screen buffer the view renders.</summary>
    public TerminalEmulator Emulator { get; }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        if (IsClosed || _settings is null) return;
        Emulator.SetScrollbackLimit(_settings.Current.TerminalScrollbackLines);
        RaiseInvalidated();
    }

    /// <summary>Raised on the UI thread after new output was parsed; coalesced per frame.</summary>
    public event EventHandler? Invalidated;

    /// <summary>Raised on the UI thread when the remote shell exits.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised before a human input is queued, so external control can be revoked immediately.</summary>
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
            new TerminalStartOptions { Columns = columns, Rows = rows, TerminalType = "xterm-256color" },
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

    /// <summary>Encodes and sends a key press.</summary>
    public void SendKey(int virtualKey, bool control = false, bool alt = false, bool shift = false)
    {
        byte[]? bytes;
        lock (Emulator.SyncRoot) bytes = TerminalInputEncoder.EncodeKey(virtualKey, control, alt, shift, Emulator.Modes);
        if (bytes is { Length: > 0 })
        {
            MarkUserInput();
            Send(bytes);
        }
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

    /// <summary>Resizes the emulator and tells the server about the new PTY size.</summary>
    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        lock (Emulator.SyncRoot)
        {
            if (Emulator.Columns == columns && Emulator.Rows == rows)
            {
                return;
            }

            Emulator.Resize(columns, rows);
        }

        await _remote.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
        RaiseInvalidated();
    }

    private void OnResponseRequested(string response) => Send(System.Text.Encoding.UTF8.GetBytes(response));

    private async void Send(byte[] bytes)
    {
        try
        {
            await _inputGate.WaitAsync().ConfigureAwait(false);
            try { if (!IsClosed) await _remote.WriteAsync(bytes).ConfigureAwait(false); }
            finally { _inputGate.Release(); }
        }
        catch (Exception ex)
        {
            // The write path wraps protocol failures in ProtocolException; an
            // async void handler must never let any of them escape unobserved.
            _logger.LogDebug(ex, "Terminal write failed; the channel is probably closed.");
        }
    }

    private void OnOutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        lock (Emulator.SyncRoot)
        {
            _output.Append(e.Data.Span);
            Emulator.Feed(e.Data.Span);
        }

        ScheduleRepaint();
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
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        Emulator.ResponseRequested -= OnResponseRequested;
        _remote.OutputReceived -= OnOutputReceived;
        _remote.Closed -= OnRemoteClosed;
        await _inputGate.WaitAsync().ConfigureAwait(false);
        try { await _remote.DisposeAsync().ConfigureAwait(false); }
        finally { _inputGate.Release(); }
    }
}
