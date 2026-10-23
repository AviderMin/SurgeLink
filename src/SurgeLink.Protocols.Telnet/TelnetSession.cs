using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Telnet;

/// <summary>A Telnet connection, exposing only its interactive terminal.</summary>
internal sealed class TelnetSession : IProtocolSession
{
    private readonly object _gate = new();
    private readonly TelnetTerminal _terminal;
    private readonly ILogger _logger;
    private ConnectionState _state = ConnectionState.Connected;
    private TaskCompletionSource? _disposeCompletion;

    public TelnetSession(TcpClient client, string host, ILogger logger)
    {
        _logger = logger;
        HostInfo = new RemoteHostInfo { HostName = host };
        _terminal = new TelnetTerminal(client, logger);
        _terminal.Closed += OnTerminalClosed;
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("n");
    public ProtocolDescriptor Protocol => TelnetProtocolPlugin.DefaultDescriptor;
    public ConnectionState State { get { lock (_gate) return _state; } }
    public RemoteHostInfo HostInfo { get; }
    public IRemoteTerminal Terminal => _terminal;
    public IRemoteFileSystem? FileSystem => null;
    public IRemoteSystemMonitor? Monitor => null;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Faulted;

    public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No shell commands or login guesses: Telnet has no out-of-band command channel.
        return Task.FromResult(HostInfo);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is null)
            {
                _disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = DisposeCoreAsync(_disposeCompletion);
            }
            return new ValueTask(_disposeCompletion.Task);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            SetState(ConnectionState.Disconnecting);
            _terminal.Closed -= OnTerminalClosed;
            await _terminal.DisposeAsync().ConfigureAwait(false);
            SetState(ConnectionState.Disconnected);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private void OnTerminalClosed(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_state is ConnectionState.Disconnecting or ConnectionState.Disconnected) return;
            if (_terminal.Failure is { } failure)
            {
                SetState(ConnectionState.Faulted, failure.Message);
                try { Faulted?.Invoke(this, failure.Message); }
                catch (Exception exception) { _logger.LogDebug(exception, "A Telnet fault handler failed."); }
            }
            else SetState(ConnectionState.Disconnected, "The remote Telnet server closed the connection.");
        }
    }

    private void SetState(ConnectionState state, string? message = null)
    {
        lock (_gate)
        {
            if (_state == state) return;
            var args = new SessionStateChangedEventArgs(_state, state, message);
            _state = state;
            try { StateChanged?.Invoke(this, args); }
            catch (Exception exception) { _logger.LogDebug(exception, "A Telnet state handler failed."); }
        }
    }
}
