using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Telnet;

/// <summary>Owns the TCP stream and serializes input with negotiation replies.</summary>
internal sealed class TelnetTerminal : IRemoteTerminal
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TelnetCodec? _codec;
    private Task? _reader;
    private TaskCompletionSource? _disposeCompletion;
    private int _columns = 120, _rows = 32, _open, _closed, _disposed;

    public TelnetTerminal(TcpClient client, ILogger logger)
    {
        _client = client;
        _stream = client.GetStream();
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
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_reader is not null) throw new InvalidOperationException("The Telnet terminal has already been started.");
            _codec = new TelnetCodec(options.Columns, options.Rows, options.TerminalType);
            Volatile.Write(ref _columns, _codec.Columns);
            Volatile.Write(ref _rows, _codec.Rows);
            Volatile.Write(ref _open, 1);
            byte[] negotiation = _codec.BeginNegotiation();
            // The connect/start token does not own the live session's lifetime.
            _reader = Task.Run(() => ReadLoopAsync(negotiation), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _ioGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _codec is null) throw new ProtocolException("The Telnet terminal is closed.", ProtocolFailureKind.Network);
            await _stream.WriteAsync(_codec.Encode(data.Span), linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            Complete(exception);
            throw new ProtocolException("Could not write to the Telnet terminal.", ProtocolFailureKind.Network, exception);
        }
        finally { _ioGate.Release(); }
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _ioGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _columns, Math.Clamp(columns, 1, ushort.MaxValue));
            Volatile.Write(ref _rows, Math.Clamp(rows, 1, ushort.MaxValue));
            if (_codec is not null && IsOpen)
            {
                byte[] reply = _codec.Resize(Columns, Rows);
                if (reply.Length > 0) await _stream.WriteAsync(reply, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            Complete(exception);
            throw new ProtocolException("Could not resize the Telnet terminal.", ProtocolFailureKind.Network, exception);
        }
        finally { _ioGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
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
            _client.Dispose();
            if (_reader is not null) await _reader.ConfigureAwait(false);
            await _ioGate.WaitAsync().ConfigureAwait(false);
            _ioGate.Release();
            Complete(null);
            _lifetime.Dispose();
            // Pending callers may still be unwinding; leave the semaphore usable.
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task ReadLoopAsync(byte[] negotiation)
    {
        Exception? failure = null;
        try
        {
            await _ioGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { await _stream.WriteAsync(negotiation, _lifetime.Token).ConfigureAwait(false); }
            finally { _ioGate.Release(); }

            var buffer = new byte[8192];
            while (!_lifetime.IsCancellationRequested)
            {
                int count = await _stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                byte[] data;
                await _ioGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    var parsed = _codec!.Receive(buffer.AsSpan(0, count));
                    data = parsed.Data;
                    if (parsed.Reply.Length > 0)
                        await _stream.WriteAsync(parsed.Reply, _lifetime.Token).ConfigureAwait(false);
                }
                finally { _ioGate.Release(); }

                if (data.Length > 0 && !_lifetime.IsCancellationRequested)
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs(data));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_lifetime.IsCancellationRequested) failure = exception;
        }
        finally { Complete(failure); }
    }

    private void Complete(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Failure = Volatile.Read(ref _disposed) == 0 ? failure : null;
        Volatile.Write(ref _open, 0);
        _client.Dispose();
        try { Closed?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _logger.LogDebug(exception, "A Telnet terminal closed handler failed."); }
    }
}
