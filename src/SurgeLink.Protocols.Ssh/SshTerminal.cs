using System.Buffers;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// Interactive shell channel backed by an SSH.NET <see cref="ShellStream"/>.
/// A dedicated background thread reads raw bytes and raises <see cref="OutputReceived"/>;
/// writes are serialised so the UI thread can call <see cref="WriteAsync"/> freely.
/// </summary>
public sealed class SshTerminal : IRemoteTerminal
{
    private const int MaximumDimension = 2000;
    private const int ReaderJoinTimeoutMilliseconds = 5000;

    private readonly SshClient _client;
    private readonly SshProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ShellStream? _stream;
    private Thread? _reader;
    private CancellationTokenSource? _readCancellation;
    private CancellationToken _readToken = CancellationToken.None;
    private int _closedRaised;
    private int _disposed;
    private int _started;
    private int _columns = 120;
    private int _rows = 32;
    private volatile bool _open;

    /// <summary>Creates a terminal channel; call <see cref="StartAsync"/> to open it.</summary>
    internal SshTerminal(SshClient client, SshProtocolOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Columns => Volatile.Read(ref _columns);

    /// <inheritdoc />
    public int Rows => Volatile.Read(ref _rows);

    /// <inheritdoc />
    public bool IsOpen => _open && !IsDisposed && _client.IsConnected;

    /// <inheritdoc />
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler? Closed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Opens the shell channel with a pseudo terminal of the requested size.
    /// The SSH protocol has no portable way to request environment variables, so
    /// <see cref="TerminalStartOptions.Environment"/> is not forwarded.
    /// </summary>
    /// <param name="options">Initial terminal size and TERM value.</param>
    /// <param name="cancellationToken">Token checked before the channel is opened.</param>
    public async Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (Volatile.Read(ref _started) != 0)
            {
                throw new InvalidOperationException("The terminal channel has already been started.");
            }

            Volatile.Write(ref _started, 1);
        }

        var columns = Math.Clamp(options.Columns, 1, MaximumDimension);
        var rows = Math.Clamp(options.Rows, 1, MaximumDimension);
        var terminalType = string.IsNullOrWhiteSpace(options.TerminalType) ? _options.TerminalType : options.TerminalType;

        if (!_client.IsConnected)
        {
            ResetStarted();
            throw new ProtocolException(
                "Cannot open a shell: the SSH connection is closed.",
                ProtocolFailureKind.Network);
        }

        ShellStream stream;
        var opening = Task.Run(
            () => _client.CreateShellStream(terminalType, (uint)columns, (uint)rows, 0, 0, _options.ShellBufferSize),
            CancellationToken.None);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            stream = await opening.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = DisposeLateShellAsync(opening);
            ResetStarted();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProtocolException("Opening the remote shell timed out.", ProtocolFailureKind.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ResetStarted();
            throw SshErrorMapper.Map(exception, "open the remote shell");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            ResetStarted();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var cancellation = new CancellationTokenSource();
        Thread reader;
        lock (_gate)
        {
            if (IsDisposed)
            {
                cancellation.Dispose();
                stream.Dispose();
                ResetStarted();
                throw new ObjectDisposedException(nameof(SshTerminal));
            }

            Volatile.Write(ref _columns, columns);
            Volatile.Write(ref _rows, rows);
            _readCancellation = cancellation;
            _readToken = cancellation.Token;
            _stream = stream;
            _open = true;
            reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "SurgeLink SSH terminal reader",
            };
            _reader = reader;
        }

        reader.Start();
    }

    private async Task DisposeLateShellAsync(Task<ShellStream> opening)
    {
        try { (await opening.ConfigureAwait(false)).Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Cancelled shell opening finished."); }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (data.IsEmpty)
        {
            return;
        }

        var stream = _stream;
        if (stream is null)
        {
            throw new ProtocolException(
                "The remote shell is not open.",
                ProtocolFailureKind.Configuration);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteToStream(stream, data), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException exception)
        {
            throw new ProtocolException(
                "The remote shell closed before the input could be written.",
                ProtocolFailureKind.Network,
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ProtocolException)
        {
            throw SshErrorMapper.Map(exception, "write to the remote shell");
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    /// <inheritdoc />
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var clampedColumns = Math.Clamp(columns, 1, MaximumDimension);
        var clampedRows = Math.Clamp(rows, 1, MaximumDimension);
        Volatile.Write(ref _columns, clampedColumns);
        Volatile.Write(ref _rows, clampedRows);

        var stream = _stream;
        if (stream is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            stream.ChangeWindowSize((uint)clampedColumns, (uint)clampedRows, 0, 0);
        }
        catch (ObjectDisposedException)
        {
            // The channel closed while the viewport was resized.
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The server refused the terminal window size change.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Closes the shell channel and stops the reader thread; safe to call twice.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        var cancellation = Interlocked.Exchange(ref _readCancellation, null);
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        var stream = Interlocked.Exchange(ref _stream, null);
        try
        {
            stream?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to close the SSH shell stream cleanly.");
        }

        var reader = _reader;
        if (reader is not null && !ReferenceEquals(reader, Thread.CurrentThread) && reader.IsAlive)
        {
            _ = reader.Join(ReaderJoinTimeoutMilliseconds);
        }

        cancellation?.Dispose();
        _open = false;
        try
        {
            _writeLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        if (Volatile.Read(ref _started) != 0)
        {
            RaiseClosed();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void ReadLoop()
    {
        var stream = _stream;
        var token = _readToken;
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ShellBufferSize);
        try
        {
            while (!token.IsCancellationRequested && stream is not null)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "The SSH shell read loop stopped with an error.");
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                RaiseOutput(new ReadOnlyMemory<byte>(chunk));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _open = false;
            RaiseClosed();
        }
    }

    /// <summary>
    /// Writes one interactive chunk and flushes it. <see cref="ShellStream.WriteAsync"/>
    /// only fills the stream's internal buffer; <see cref="Stream.Flush"/> is what
    /// hands the bytes to the channel. Kept separate so the flush contract is
    /// directly testable without a live SSH server.
    /// </summary>
    internal static void WriteToStream(Stream stream, ReadOnlyMemory<byte> data)
    {
        stream.Write(data.Span);
        stream.Flush();
    }

    private void RaiseOutput(ReadOnlyMemory<byte> data)
    {
        var handler = OutputReceived;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new TerminalOutputEventArgs(data));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A terminal output handler failed.");
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0)
        {
            return;
        }

        var handler = Closed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A terminal closed handler failed.");
        }
    }

    private void ReleaseWriteLock()
    {
        try
        {
            _writeLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // The terminal was disposed while a write was in flight.
        }
    }

    private void ResetStarted()
    {
        lock (_gate)
        {
            Volatile.Write(ref _started, 0);
        }
    }
}
