using Microsoft.Extensions.Logging;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Samples a Windows guest by running one PowerShell script per snapshot and parsing the
/// delimited sections it prints.
/// </summary>
/// <remarks>
/// The guest needs <c>powershell.exe</c> (Windows PowerShell 5.1 or PowerShell 7) on the PATH
/// of the SSH user. Sections that cannot be read degrade to zeroed values; only a completely
/// unusable command result is reported as a <see cref="ProtocolException"/>.
/// </remarks>
public sealed class WindowsMonitorProbe : IRemoteSystemMonitor, IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task<SshCommandResult>> _executeCommand;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _sampleLock = new(1, 1);
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private int _disposed;

    /// <summary>Creates a probe over an arbitrary command executor.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    public WindowsMonitorProbe(Func<string, CancellationToken, Task<SshCommandResult>> executeCommand)
        : this(executeCommand, null)
    {
    }

    /// <summary>Creates a probe over an arbitrary command executor with optional logging.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    /// <param name="logger">Logger used for degraded probes; may be <c>null</c>.</param>
    public WindowsMonitorProbe(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(executeCommand);
        _executeCommand = executeCommand;
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sampleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SshCommandResult result;
            try
            {
                result = await _executeCommand(WindowsMonitorScript.SampleCommand, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw SshErrorMapper.Map(exception, "collect Windows system counters");
            }

            var sections = MonitorOutput.Split(result.StandardOutput);
            if (sections.Count == 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? string.Empty
                    : " " + Describe(result.StandardError);
                throw new ProtocolException(
                    $"The remote monitor command did not return any usable section (exit status {result.ExitStatus?.ToString() ?? "unknown"}).{detail}",
                    ProtocolFailureKind.Unknown);
            }

            var now = DateTimeOffset.UtcNow;
            var cpu = WindowsCpuParser.Parse(
                ReadSection(sections, WindowsMonitorScript.CpuSection),
                ReadSection(sections, WindowsMonitorScript.ProcessorSection));
            var memory = WindowsMemoryParser.Parse(ReadSection(sections, WindowsMonitorScript.MemorySection));
            var system = WindowsSystemParser.Parse(ReadSection(sections, WindowsMonitorScript.SystemSection), now);
            var disks = WindowsDiskParser.Parse(ReadSection(sections, WindowsMonitorScript.DiskSection));
            var networks = WindowsNetworkParser.Parse(ReadSection(sections, WindowsMonitorScript.NetworkSection));
            if (networks.Count == 0)
            {
                networks = WindowsNetworkParser.Parse(ReadSection(sections, WindowsMonitorScript.NetworkFallbackSection));
            }

            return new SystemSnapshot
            {
                TimestampUtc = now,
                Cpu = new CpuSample
                {
                    TotalPercent = cpu.TotalPercent,
                    PerCorePercent = cpu.PerCorePercent,
                    ProcessCount = system.ProcessCount,
                    ThreadCount = system.ThreadCount,
                },
                Memory = memory,
                Disks = disks,
                Networks = networks,
                Uptime = system.Uptime,
            };
        }
        finally
        {
            SafeRelease(_sampleLock);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<RemoteProcessInfo>();
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var command in WindowsMonitorScript.BuildTopProcessCommands(maxCount))
            {
                SshCommandResult result;
                try
                {
                    result = await _executeCommand(command, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger?.LogDebug(exception, "Windows top process command failed.");
                    continue;
                }

                var processes = WindowsProcessParser.Parse(result.StandardOutput);
                if (processes.Count > 0)
                {
                    return PsParser.TopByCpu(processes, maxCount);
                }
            }

            return Array.Empty<RemoteProcessInfo>();
        }
        finally
        {
            SafeRelease(_processLock);
        }
    }

    /// <summary>Releases the internal synchronisation primitives; safe to call twice.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        SafeDispose(_sampleLock);
        SafeDispose(_processLock);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static string Describe(string text) =>
        text.Length > 200 ? text[..200].Trim() + "..." : text.Trim();

    private static string? ReadSection(IReadOnlyDictionary<string, string> sections, string name) =>
        sections.TryGetValue(name, out var content) ? content : null;

    private static void SafeRelease(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // The probe was disposed while an operation was running.
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