namespace SurgeLink.Core.Monitoring;

/// <summary>
/// Samples remote performance counters. Implementations are responsible for
/// turning a single round trip into one <see cref="SystemSnapshot"/> whenever
/// the protocol allows it.
/// </summary>
public interface IRemoteSystemMonitor
{
    /// <summary>Sampling interval hint used by the UI (the UI may override it).</summary>
    TimeSpan RecommendedInterval { get; }

    /// <summary>Takes one snapshot of CPU, memory, disks and network.</summary>
    Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken);

    /// <summary>Returns the busiest processes, ordered by CPU usage.</summary>
    Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken);
}
