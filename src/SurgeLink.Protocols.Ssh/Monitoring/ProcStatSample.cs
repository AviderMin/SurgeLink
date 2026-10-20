namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>One parsed sample of <c>/proc/stat</c>: the aggregate line plus one entry per core.</summary>
public sealed record ProcStatSample
{
    /// <summary>An empty sample used before the first successful read.</summary>
    public static ProcStatSample Empty { get; } = new();

    /// <summary>Aggregate counters from the <c>cpu</c> line.</summary>
    public ProcCpuTimes Total { get; init; }

    /// <summary>Per core counters from the <c>cpuN</c> lines, index aligned to the core number.</summary>
    public IReadOnlyList<ProcCpuTimes> PerCore { get; init; } = Array.Empty<ProcCpuTimes>();
}
