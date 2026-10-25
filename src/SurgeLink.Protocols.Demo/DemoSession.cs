using Microsoft.Extensions.Logging;
using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Demo;

/// <summary>Offline session: fake shell + in-memory files + synthetic monitor.</summary>
public sealed class DemoSession : IProtocolSession
{
    private readonly ILogger _logger;
    private readonly DemoTerminal _terminal;
    private readonly DemoFileSystem _fileSystem;
    private readonly DemoMonitor _monitor;
    private ConnectionState _state = ConnectionState.Connecting;

    internal DemoSession(ConnectionRequest request, ILogger logger)
    {
        _logger = logger;
        _terminal = new DemoTerminal(request.Username, request.Host);
        _terminal.Closed += OnTerminalClosed;
        _fileSystem = new DemoFileSystem();
        _monitor = new DemoMonitor();
        _state = ConnectionState.Connected;
    }

    /// <inheritdoc />
    public string SessionId { get; } = Guid.NewGuid().ToString("n");

    /// <inheritdoc />
    public ProtocolDescriptor Protocol { get; } = new(
        "demo",
        "Demo (offline)",
        0,
        SessionCapabilities.Terminal | SessionCapabilities.FileSystem | SessionCapabilities.Monitor);

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public RemoteHostInfo HostInfo { get; } = new()
    {
        HostName = "demo-host",
        OperatingSystem = "SurgeLink Demo Linux 6.6",
        KernelVersion = "6.6.0-demo",
        Architecture = "x86_64",
        HomeDirectory = "/home/demo",
        Shell = "/bin/demo-sh",
        ServerVersion = "SurgeLink-Demo_1.0",
    };

    /// <inheritdoc />
    public IRemoteTerminal Terminal => _terminal;

    /// <inheritdoc />
    public IRemoteFileSystem FileSystem => _fileSystem;

    /// <inheritdoc />
    public IRemoteSystemMonitor Monitor => _monitor;

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

#pragma warning disable CS0067 // The demo session never faults; the event exists to satisfy IProtocolSession.
    /// <inheritdoc />
    public event EventHandler<string>? Faulted;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken) => Task.FromResult(HostInfo);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _terminal.DisposeAsync().ConfigureAwait(false);
        _terminal.Closed -= OnTerminalClosed;
        SetState(ConnectionState.Disconnected, "Session closed");
    }

    private void OnTerminalClosed(object? sender, EventArgs e) =>
        SetState(ConnectionState.Disconnected, "Remote shell exited.");

    private void SetState(ConnectionState next, string? message = null)
    {
        var previous = _state;
        if (previous == next)
        {
            return;
        }

        _state = next;
        _logger.LogDebug("Demo session {Id}: {Previous} -> {Next}", SessionId, previous, next);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(previous, next, message));
    }
}
