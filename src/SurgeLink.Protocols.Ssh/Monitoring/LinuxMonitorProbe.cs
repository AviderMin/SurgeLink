using Microsoft.Extensions.Logging;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Samples a Linux host by executing one POSIX shell script per snapshot and parsing the
/// collected <c>/proc</c> files. Works against busybox and procps based systems.
/// </summary>
public sealed class LinuxMonitorProbe : IRemoteSystemMonitor, IAsyncDisposable
{
    private static readonly string[] TopProcessCommands =
    {
        "LC_ALL=C ps -eo pid,user,pcpu,pmem,rss,comm --sort=-pcpu 2>/dev/null",
        "LC_ALL=C ps -eo pid,user,pcpu,pmem,rss,args --sort=-pcpu 2>/dev/null",
        "LC_ALL=C ps aux 2>/dev/null",
        "LC_ALL=C ps 2>/dev/null",
    };

    private readonly Func<string, CancellationToken, Task<SshCommandResult>> _executeCommand;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _sampleLock = new(1, 1);
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private ProcStatSample? _previousStat;
    private IReadOnlyList<DiskIoStat> _lastDiskIoStatistics = Array.Empty<DiskIoStat>();
    private DateTimeOffset? _lastDiskIoSampleUtc;
    private long _lastDiskReadBytes;
    private long _lastDiskWriteBytes;
    private int _disposed;

    /// <summary>Creates a probe over an arbitrary command executor.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    public LinuxMonitorProbe(Func<string, CancellationToken, Task<SshCommandResult>> executeCommand)
        : this(executeCommand, null)
    {
    }

    /// <summary>Creates a probe over an arbitrary command executor with optional logging.</summary>
    /// <param name="executeCommand">Runs a remote command and returns its decoded output.</param>
    /// <param name="logger">Logger used for degraded probes; may be <c>null</c>.</param>
    public LinuxMonitorProbe(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(executeCommand);
        _executeCommand = executeCommand;
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(2);

    /// <summary>Per device I/O counters parsed from <c>/proc/diskstats</c> by the last sample.</summary>
    public IReadOnlyList<DiskIoStat> LastDiskIoStatistics => Volatile.Read(ref _lastDiskIoStatistics);

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
                result = await _executeCommand(MonitorScript.Command, cancellationToken).ConfigureAwait(false);
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
                throw SshErrorMapper.Map(exception, "collect system counters");
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

            var stat = ReadSection(sections, MonitorScript.StatSection, ProcStatParser.Parse);
            var usage = CpuUsageCalculator.Compute(_previousStat, stat);
            if (stat is not null)
            {
                _previousStat = stat;
            }

            var memory = ReadSection(sections, MonitorScript.MemInfoSection, MemInfoParser.Parse) ?? new MemorySample();
            var load = ReadSection(sections, MonitorScript.LoadAvgSection, LoadAvgParser.Parse);
            var uptime = sections.TryGetValue(MonitorScript.UptimeSection, out var uptimeText)
                ? UptimeParser.Parse(uptimeText)
                : null;
            var networks = ReadSection(sections, MonitorScript.NetDevSection, NetDevParser.Parse) ?? Array.Empty<NetworkSample>();
            var disks = ReadSection(sections, MonitorScript.DiskUsageSection, text => DfParser.Parse(text)) ?? Array.Empty<DiskSample>();
            var diskIo = ReadSection(sections, MonitorScript.DiskStatsSection, DiskStatsParser.Parse);
            if (diskIo is not null)
            {
                Volatile.Write(ref _lastDiskIoStatistics, diskIo);
            }

            var sampledAt = DateTimeOffset.UtcNow;
            var diskReadRate = 0d;
            var diskWriteRate = 0d;
            if (diskIo is not null)
            {
                var (readSectors, writeSectors) = DiskStatsParser.SumWholeDeviceSectors(diskIo);
                var readBytes = readSectors * 512L;
                var writeBytes = writeSectors * 512L;
                if (_lastDiskIoSampleUtc is { } previous)
                {
                    var seconds = (sampledAt - previous).TotalSeconds;
                    if (seconds > 0.05)
                    {
                        diskReadRate = readBytes >= _lastDiskReadBytes
                            ? (readBytes - _lastDiskReadBytes) / seconds
                            : 0d;
                        diskWriteRate = writeBytes >= _lastDiskWriteBytes
                            ? (writeBytes - _lastDiskWriteBytes) / seconds
                            : 0d;
                    }
                }

                _lastDiskIoSampleUtc = sampledAt;
                _lastDiskReadBytes = readBytes;
                _lastDiskWriteBytes = writeBytes;
            }

            return new SystemSnapshot
            {
                TimestampUtc = sampledAt,
                Cpu = new CpuSample
                {
                    TotalPercent = usage.TotalPercent,
                    PerCorePercent = usage.PerCorePercent,
                    Load1 = load?.Load1 ?? 0d,
                    Load5 = load?.Load5 ?? 0d,
                    Load15 = load?.Load15 ?? 0d,
                    ProcessCount = load?.RunnableCount ?? 0,
                    ThreadCount = load?.TotalThreads ?? 0,
                },
                Memory = memory,
                Disks = disks,
                Networks = networks,
                DiskReadBytesPerSecond = diskReadRate,
                DiskWriteBytesPerSecond = diskWriteRate,
                Uptime = uptime,
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
            foreach (var command in TopProcessCommands)
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
                    _logger?.LogDebug(exception, "Top process command failed: {Command}", command);
                    continue;
                }

                var processes = PsParser.Parse(result.StandardOutput);
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

    private static T? ReadSection<T>(IReadOnlyDictionary<string, string> sections, string name, Func<string?, T?> parser)
        where T : class
    {
        return sections.TryGetValue(name, out var content) ? parser(content) : null;
    }

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