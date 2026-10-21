using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;
using SurgeLink.Protocols.Ssh.Internal;
using SurgeLink.Protocols.Ssh.Monitoring;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// A live SSH connection. Owns the <see cref="SshClient"/> and creates the terminal,
/// SFTP/SCP file system and monitor capabilities on demand.
/// </summary>
public sealed class SshSession : IProtocolSession
{
    private const string LinuxHostNameCommand = "uname -n";
    private const string WindowsHostNameCommand = "hostname";
    private const string OsReleaseCommand = "cat /etc/os-release 2>/dev/null";
    private const string ShellCommand = "echo $SHELL";
    private const string WorkingDirectoryCommand = "pwd";

    private readonly SshClient _client;
    private readonly SshConnectionFactory _factory;
    private readonly SshProtocolOptions _options;
    private readonly ProtocolDescriptor _descriptor;
    private readonly SshCommandRunner _runner;
    private readonly ILogger<SshSession> _logger;
    private readonly object _gate = new();
    private readonly string _sessionId;

    private ConnectionState _state = ConnectionState.Connecting;
    private RemoteHostInfo _hostInfo = RemoteHostInfo.Empty;
    private Task<RemoteHostInfo>? _hostInfoTask;
    private SshTerminal? _terminal;
    private SshFileSystem? _fileSystem;
    private OsAwareMonitor? _monitor;
    private int _remoteOperatingSystem = (int)RemoteOperatingSystem.Unknown;
    private int _disposed;

    /// <summary>Creates a session over an already connected client.</summary>
    internal SshSession(
        SshClient client,
        SshConnectionFactory factory,
        SshProtocolOptions options,
        ProtocolDescriptor descriptor,
        ILogger<SshSession> logger)
    {
        _client = client;
        _factory = factory;
        _options = options;
        _descriptor = descriptor;
        _logger = logger;
        _sessionId = Guid.NewGuid().ToString("n");
        _runner = new SshCommandRunner(client, options.Encoding, options.CommandTimeout);
        _client.ErrorOccurred += OnTransportError;
        SetState(ConnectionState.Connected);
    }

    /// <inheritdoc />
    public string SessionId => _sessionId;

    /// <inheritdoc />
    public ProtocolDescriptor Protocol => _descriptor;

    /// <inheritdoc />
    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public RemoteHostInfo HostInfo
    {
        get
        {
            lock (_gate)
            {
                return _hostInfo;
            }
        }
    }

    /// <inheritdoc />
    public IRemoteTerminal? Terminal
    {
        get
        {
            if (IsDisposed)
            {
                return null;
            }

            lock (_gate)
            {
                return _terminal ??= new SshTerminal(_client, _options, _logger);
            }
        }
    }

    /// <inheritdoc />
    public IRemoteFileSystem? FileSystem
    {
        get
        {
            if (IsDisposed)
            {
                return null;
            }

            lock (_gate)
            {
                return _fileSystem ??= new SshFileSystem(_factory, _options, _logger);
            }
        }
    }

    /// <inheritdoc />
    public IRemoteSystemMonitor? Monitor
    {
        get
        {
            if (IsDisposed)
            {
                return null;
            }

            lock (_gate)
            {
                return _monitor ??= new OsAwareMonitor(_runner.RunAsync, GetRemoteOperatingSystemAsync, _logger);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return Task.FromResult(HostInfo);
        }

        lock (_gate)
        {
            return _hostInfoTask ??= ProbeHostInfoAsync(cancellationToken);
        }
    }

    /// <summary>Closes every capability and the underlying SSH client; safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SetState(ConnectionState.Disconnecting);

        SshTerminal? terminal;
        SshFileSystem? fileSystem;
        OsAwareMonitor? monitor;
        lock (_gate)
        {
            terminal = _terminal;
            fileSystem = _fileSystem;
            monitor = _monitor;
            _terminal = null;
            _fileSystem = null;
            _monitor = null;
        }

        if (terminal is not null)
        {
            try
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to close the terminal of {SessionId}.", _sessionId);
            }
        }

        if (fileSystem is not null)
        {
            try
            {
                await fileSystem.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to close the file system of {SessionId}.", _sessionId);
            }
        }

        if (monitor is not null)
        {
            try
            {
                await monitor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to close the monitor of {SessionId}.", _sessionId);
            }
        }

        _client.ErrorOccurred -= OnTransportError;
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to disconnect {SessionId} cleanly.", _sessionId);
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to dispose {SessionId} cleanly.", _sessionId);
        }

        SetState(ConnectionState.Disconnected);
    }

    private async Task<RemoteHostInfo> ProbeHostInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _hostInfo = info;
            }

            return info;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _hostInfoTask = null;
            }

            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Host information probe for {SessionId} failed.", _sessionId);
            return HostInfo;
        }
    }

    private async Task<RemoteHostInfo> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        var info = RemoteHostInfo.Empty with { ServerVersion = _client.ConnectionInfo.ServerVersion };

        var uname = await TryRunAsync(RemoteOsDetector.UnameCommand, cancellationToken).ConfigureAwait(false);
        var windowsVersion = await TryRunAsync(RemoteOsDetector.WindowsVersionCommand, cancellationToken).ConfigureAwait(false);
        var operatingSystem = RemoteOsDetector.Classify(uname, windowsVersion);
        Interlocked.CompareExchange(
            ref _remoteOperatingSystem,
            (int)operatingSystem,
            (int)RemoteOperatingSystem.Unknown);

        if (operatingSystem == RemoteOperatingSystem.Windows)
        {
            var version = RemoteOsDetector.ParseWindowsVersion(windowsVersion);
            if (!string.IsNullOrWhiteSpace(version))
            {
                info = info with
                {
                    OperatingSystem = "Windows " + version,
                    KernelVersion = version,
                };
            }

            var windowsHostName = await TryRunAsync(WindowsHostNameCommand, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(windowsHostName))
            {
                info = info with { HostName = windowsHostName!.Trim() };
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(uname))
            {
                var tokens = uname!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 3)
                {
                    info = info with
                    {
                        OperatingSystem = tokens[0],
                        KernelVersion = tokens[1],
                        Architecture = tokens[2],
                    };
                }
                else if (tokens.Length > 0)
                {
                    info = info with { OperatingSystem = tokens[0] };
                }
            }

            var hostName = await TryRunAsync(LinuxHostNameCommand, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                info = info with { HostName = hostName!.Trim() };
            }

            var osRelease = await TryRunAsync(OsReleaseCommand, cancellationToken).ConfigureAwait(false);
            var prettyName = ParsePrettyName(osRelease);
            if (!string.IsNullOrWhiteSpace(prettyName))
            {
                info = info with { OperatingSystem = prettyName };
            }

            var shell = await TryRunAsync(ShellCommand, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(shell))
            {
                info = info with { Shell = shell!.Trim() };
            }

            var workingDirectory = await TryRunAsync(WorkingDirectoryCommand, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                info = info with { HomeDirectory = workingDirectory!.Trim() };
            }
        }

        if (string.IsNullOrWhiteSpace(info.HostName))
        {
            info = info with { HostName = _client.ConnectionInfo.Host };
        }

        return info;
    }

    private async Task<RemoteOperatingSystem> GetRemoteOperatingSystemAsync(CancellationToken cancellationToken)
    {
        var cached = (RemoteOperatingSystem)Volatile.Read(ref _remoteOperatingSystem);
        if (cached != RemoteOperatingSystem.Unknown)
        {
            return cached;
        }

        var detected = await RemoteOsDetector.DetectAsync(_runner.RunAsync, cancellationToken).ConfigureAwait(false);
        Interlocked.CompareExchange(
            ref _remoteOperatingSystem,
            (int)detected,
            (int)RemoteOperatingSystem.Unknown);
        return detected;
    }

    private async Task<string?> TryRunAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            return result.StandardOutput.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Remote probe command failed: {Command}", command);
            return null;
        }
    }

    private static string? ParsePrettyName(string? osRelease)
    {
        if (string.IsNullOrWhiteSpace(osRelease))
        {
            return null;
        }

        foreach (var rawLine in osRelease!.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                continue;
            }

            return line["PRETTY_NAME=".Length..].Trim().Trim('"', '\'');
        }

        return null;
    }

    private void OnTransportError(object? sender, ExceptionEventArgs args)
    {
        if (IsDisposed)
        {
            return;
        }

        var message = args.Exception?.Message ?? "SSH transport error.";
        _logger.LogWarning(args.Exception, "SSH transport error on {SessionId}.", _sessionId);
        RaiseFaulted(message);

        if (!_client.IsConnected)
        {
            SetState(ConnectionState.Faulted, message);
        }
    }

    private void SetState(ConnectionState newState, string? message = null)
    {
        SessionStateChangedEventArgs args;
        lock (_gate)
        {
            if (_state == newState)
            {
                return;
            }

            args = new SessionStateChangedEventArgs(_state, newState, message);
            _state = newState;
        }

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A session state handler failed.");
        }
    }

    private void RaiseFaulted(string message)
    {
        var handler = Faulted;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, message);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A session faulted handler failed.");
        }
    }
}