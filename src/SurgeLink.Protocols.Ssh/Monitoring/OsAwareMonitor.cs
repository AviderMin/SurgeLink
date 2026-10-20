using Microsoft.Extensions.Logging;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Picks the guest specific monitor on first use: <see cref="LinuxMonitorProbe"/> for Linux
/// and <see cref="WindowsMonitorProbe"/> for Windows. Any other guest fails with a
/// <see cref="ProtocolException"/> instead of silently returning empty data.
/// </summary>
public sealed class OsAwareMonitor : IRemoteSystemMonitor, IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task<SshCommandResult>> _executeCommand;
    private readonly Func<CancellationToken, Task<RemoteOperatingSystem>> _detectOperatingSystem;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _selectionLock = new(1, 1);
    private IRemoteSystemMonitor? _probe;
    private int _operatingSystem = (int)RemoteOperatingSystem.Unknown;
    private int _disposed;

    /// <summary>Creates a monitor that detects the guest itself.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    public OsAwareMonitor(Func<string, CancellationToken, Task<SshCommandResult>> executeCommand)
        : this(executeCommand, null, null)
    {
    }

    /// <summary>Creates a monitor with an injected guest detector, used by the session to reuse its cached OS.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    /// <param name="detectOperatingSystem">Detector used when the guest family is not known yet; <c>null</c> runs the default probes.</param>
    /// <param name="logger">Logger used for degraded samples; may be <c>null</c>.</param>
    public OsAwareMonitor(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        Func<CancellationToken, Task<RemoteOperatingSystem>>? detectOperatingSystem,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(executeCommand);
        _executeCommand = executeCommand;
        _detectOperatingSystem = detectOperatingSystem ?? (token => RemoteOsDetector.DetectAsync(executeCommand, token));
        _logger = logger;
    }

    /// <summary>Creates the OS aware monitor used by <see cref="SshSession.Monitor"/>.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    /// <param name="logger">Logger used for degraded samples; may be <c>null</c>.</param>
    public static IRemoteSystemMonitor Create(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        ILogger? logger = null) =>
        new OsAwareMonitor(executeCommand, null, logger);

    /// <inheritdoc />
    public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(2);

    /// <summary>Guest family, or <see cref="RemoteOperatingSystem.Unknown"/> until the first sample.</summary>
    public RemoteOperatingSystem OperatingSystem => (RemoteOperatingSystem)Volatile.Read(ref _operatingSystem);

    /// <summary>The probe selected for the detected guest, or <c>null</c> until the first sample.</summary>
    public IRemoteSystemMonitor? ActiveProbe => Volatile.Read(ref _probe);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public async Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        var probe = await SelectProbeAsync(cancellationToken).ConfigureAwait(false);
        return await probe.SampleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken)
    {
        var probe = await SelectProbeAsync(cancellationToken).ConfigureAwait(false);
        return await probe.GetTopProcessesAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the selected probe; safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var probe = Interlocked.Exchange(ref _probe, null);
        if (probe is IAsyncDisposable disposable)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Failed to dispose the selected remote monitor probe.");
            }
        }

        SafeDispose(_selectionLock);
        GC.SuppressFinalize(this);
    }

    private async Task<IRemoteSystemMonitor> SelectProbeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var existing = ActiveProbe;
        if (existing is not null)
        {
            return existing;
        }

        await _selectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            existing = ActiveProbe;
            if (existing is not null)
            {
                return existing;
            }

            var operatingSystem = OperatingSystem;
            if (operatingSystem == RemoteOperatingSystem.Unknown)
            {
                operatingSystem = await _detectOperatingSystem(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _operatingSystem, (int)operatingSystem);
            }

            var probe = operatingSystem switch
            {
                RemoteOperatingSystem.Linux => (IRemoteSystemMonitor)new LinuxMonitorProbe(_executeCommand, _logger),
                RemoteOperatingSystem.Windows => new WindowsMonitorProbe(_executeCommand, _logger),
                _ => throw new ProtocolException(
                    "The remote operating system could not be identified; remote monitoring supports Linux and Windows guests only.",
                    ProtocolFailureKind.Unknown),
            };

            _logger?.LogDebug("Remote monitor selected {Probe} for a {OperatingSystem} guest.", probe.GetType().Name, operatingSystem);
            Volatile.Write(ref _probe, probe);
            return probe;
        }
        finally
        {
            SafeRelease(_selectionLock);
        }
    }

    private static void SafeRelease(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // The monitor was disposed while a probe was being selected.
        }
    }

    private static void SafeDispose(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }
}
