namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>CPU utilisation derived from two <c>/proc/stat</c> samples.</summary>
public sealed record CpuUsage
{
    /// <summary>A usage value used when no previous sample exists.</summary>
    public static CpuUsage Empty { get; } = new();

    /// <summary>Aggregate utilisation across all cores, 0..100.</summary>
    public double TotalPercent { get; init; }

    /// <summary>Per core utilisation, 0..100. Empty proportions are reported as zero.</summary>
    public IReadOnlyList<double> PerCorePercent { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Derives utilisation percentages from the delta between two <c>/proc/stat</c> samples.
/// The first sample has no predecessor and therefore reports zero everywhere.
/// </summary>
public static class CpuUsageCalculator
{
    /// <summary>Computes utilisation from the previous and the current sample.</summary>
    /// <param name="previous">Previous sample, or <c>null</c> for the first one.</param>
    /// <param name="current">Current sample, or <c>null</c> when <c>/proc/stat</c> could not be read.</param>
    public static CpuUsage Compute(ProcStatSample? previous, ProcStatSample? current)
    {
        if (current is null)
        {
            return CpuUsage.Empty;
        }

        var perCore = new double[current.PerCore.Count];
        for (var i = 0; i < perCore.Length; i++)
        {
            perCore[i] = previous is not null && i < previous.PerCore.Count
                ? ComputePercent(previous.PerCore[i], current.PerCore[i])
                : 0d;
        }

        return new CpuUsage
        {
            TotalPercent = previous is null ? 0d : ComputePercent(previous.Total, current.Total),
            PerCorePercent = perCore,
        };
    }

    private static double ComputePercent(ProcCpuTimes previous, ProcCpuTimes current)
    {
        var delta = ProcCpuTimes.Delta(previous, current);
        var total = delta.Total;
        if (total <= 0)
        {
            return 0d;
        }

        var busy = total - delta.IdleTotal;
        if (busy <= 0)
        {
            return 0d;
        }

        return Math.Clamp(busy * 100d / total, 0d, 100d);
    }
}
