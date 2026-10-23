using Microsoft.Extensions.Logging;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Serial;

/// <summary>Raw serial bytes: no Telnet negotiation, newline rewriting or shell probing.</summary>
internal sealed class SerialTerminal : IRemoteTerminal
{
    private readonly ISerialConnection _connection;
    private readonly ILogger _logger;
    private readonly string _portName;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reader;
    private TaskCompletionSource? _disposeCompletion;
    private int _columns = 120, _rows = 32, _open, _closed, _disposed;

    public SerialTerminal(ISerialConnection connection, string portName, ILogger logger)
    {
        _connection = connection;
        _portName = portName;
        _logger = logger;
    }

    public int Columns => Volatile.Read(ref _columns);
    public int Rows => Volatile.Read(ref _rows);
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    internal Exception? Failure { get; private set; }
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    public event EventHandler? Closed;

    public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_reader is not null) throw new InvalidOperationException("The serial terminal has already been started.");
            Volatile.Write(ref _columns, Math.Clamp(options.Columns, 1, ushort.MaxValue));
            Volatile.Write(ref _rows, Math.Clamp(options.Rows, 1, ushort.MaxValue));
            Volatile.Write(ref _open, 1);
            // SerialPort.Read is bounded by ReadTimeout; do not depend on BaseStream cancellation.
            _reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!IsOpen) throw new ProtocolException("The serial terminal is closed.", ProtocolFailureKind.Network);
            // Once a write starts, keep ownership until its bounded driver call finishes.
            // Releasing the gate on cancellation would let the next write interleave bytes.
            await Task.Run(() => _connection.Write(data.ToArray()), linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Complete(exception);
            throw SerialProtocolPlugin.MapError(exception, _portName);
        }
        finally { _writeGate.Release(); }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _columns, Math.Clamp(columns, 1, ushort.MaxValue));
        Volatile.Write(ref _rows, Math.Clamp(rows, 1, ushort.MaxValue));
        // A serial console has no PTY resize message; only the local viewport changes.
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is null)
            {
                _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _disposed, 1);
                _ = DisposeCoreAsync(_disposeCompletion);
            }
            return new ValueTask(_disposeCompletion.Task);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            _lifetime.Cancel();
            if (_reader is not null) await _reader.ConfigureAwait(false);
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try { await Task.Run(_connection.Dispose).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            Complete(null);
            _lifetime.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            Complete(exception);
            completion.TrySetException(exception);
        }
    }

    private void ReadLoop()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[4096];
            while (!_lifetime.IsCancellationRequested)
            {
                int count;
                try { count = _connection.Read(buffer); }
                catch (TimeoutException) { continue; } // Idle port, not a disconnection.
                if (count == 0) break;
                if (!_lifetime.IsCancellationRequested)
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs(buffer.AsMemory(0, count).ToArray()));
            }
        }
        catch (Exception exception)
        {
            if (!_lifetime.IsCancellationRequested) failure = exception;
        }
        finally
        {
            Complete(failure);
        }
    }

    private void Complete(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Failure = Volatile.Read(ref _disposed) == 0 ? failure : null;
        Volatile.Write(ref _open, 0);
        try { Closed?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _logger.LogDebug(exception, "A serial terminal closed handler failed."); }
        _ = DisposeAfterCloseAsync();
    }

    private async Task DisposeAfterCloseAsync()
    {
        // Release failed/unplugged ports even if the workspace tab remains visible.
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogDebug(exception, "Failed to release serial port {Port}.", _portName); }
    }
}
