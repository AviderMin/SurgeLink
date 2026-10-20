namespace SurgeLink.Core.Monitoring;

/// <summary>One sample of remote system counters.</summary>
public sealed record SystemSnapshot
{
    /// <summary>When the sample was taken (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public CpuSample Cpu { get; init; } = new();

    public MemorySample Memory { get; init; } = new();

    /// <summary>Mounted file systems with usage percentages.</summary>
    public IReadOnlyList<DiskSample> Disks { get; init; } = Array.Empty<DiskSample>();

    /// <summary>Network interfaces with cumulative byte counters.</summary>
    public IReadOnlyList<NetworkSample> Networks { get; init; } = Array.Empty<NetworkSample>();

    /// <summary>Host uptime when reported.</summary>
    public TimeSpan? Uptime { get; init; }
}

/// <summary>CPU utilisation sample.</summary>
public sealed record CpuSample
{
    /// <summary>Total utilisation across all cores, 0..100.</summary>
    public double TotalPercent { get; init; }

    /// <summary>Per core utilisation, 0..100. Empty when the server reports only totals.</summary>
    public IReadOnlyList<double> PerCorePercent { get; init; } = Array.Empty<double>();

    public double Load1 { get; init; }

    public double Load5 { get; init; }

    public double Load15 { get; init; }

    public int ProcessCount { get; init; }

    public int ThreadCount { get; init; }
}

/// <summary>Memory and swap usage in bytes.</summary>
public sealed record MemorySample
{
    public long TotalBytes { get; init; }

    public long AvailableBytes { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

    public long CachedBytes { get; init; }

    public long SwapTotalBytes { get; init; }

    public long SwapUsedBytes { get; init; }

    /// <summary>Used physical memory as a fraction, 0..1.</summary>
    public double UsedFraction => TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0d, 1d) : 0d;
}

/// <summary>Disk usage of one mount point.</summary>
public sealed record DiskSample
{
    public required string MountPoint { get; init; }

    public string? FileSystem { get; init; }

    public long TotalBytes { get; init; }

    public long UsedBytes { get; init; }

    public long AvailableBytes { get; init; }

    public double UsedFraction => TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0d, 1d) : 0d;
}

/// <summary>Cumulative network counters; rates are derived by the monitor service.</summary>
public sealed record NetworkSample
{
    public required string InterfaceName { get; init; }

    public long BytesReceived { get; init; }

    public long BytesSent { get; init; }

    public long PacketsReceived { get; init; }

    public long PacketsSent { get; init; }
}

/// <summary>A process row in the top processes list.</summary>
public sealed record RemoteProcessInfo
{
    public int ProcessId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? User { get; init; }

    public double CpuPercent { get; init; }

    public double MemoryPercent { get; init; }

    public long ResidentBytes { get; init; }

    public string? CommandLine { get; init; }
}
