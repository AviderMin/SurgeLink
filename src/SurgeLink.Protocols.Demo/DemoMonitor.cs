using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Demo;

/// <summary>Synthetic performance counters with a slow sine wave so charts animate.</summary>
public sealed class DemoMonitor : IRemoteSystemMonitor
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        var elapsed = (DateTimeOffset.UtcNow - _started).TotalSeconds;
        var wave = Math.Sin(elapsed / 6.0);
        var wave2 = Math.Sin(elapsed / 3.5 + 1.2);

        var cores = new List<double>(8);
        for (var i = 0; i < 8; i++)
        {
            cores.Add(Math.Clamp(35 + (wave * 22) + (wave2 * 6 * Math.Sin(i + elapsed / 4.0)), 1, 100));
        }

        const long totalMemory = 16L * 1024 * 1024 * 1024;
        var usedMemory = (long)(totalMemory * (0.42 + (wave2 * 0.06)));

        var snapshot = new SystemSnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Cpu = new CpuSample
            {
                TotalPercent = Math.Clamp(35 + (wave * 20) + (wave2 * 5), 1, 100),
                PerCorePercent = cores,
                Load1 = 0.82 + (wave * 0.3),
                Load5 = 0.64 + (wave2 * 0.2),
                Load15 = 0.55 + (wave * 0.1),
                ProcessCount = 236 + (int)(wave * 8),
                ThreadCount = 980 + (int)(wave2 * 30),
            },
            Memory = new MemorySample
            {
                TotalBytes = totalMemory,
                AvailableBytes = totalMemory - usedMemory,
                CachedBytes = 3L * 1024 * 1024 * 1024,
                SwapTotalBytes = 4L * 1024 * 1024 * 1024,
                SwapUsedBytes = (long)(4L * 1024 * 1024 * 1024 * 0.12),
            },
            Disks = new List<DiskSample>
            {
                new() { MountPoint = "/", FileSystem = "/dev/nvme0n1p2", TotalBytes = 512L * 1024 * 1024 * 1024, UsedBytes = (long)(512L * 1024 * 1024 * 1024 * 0.63), AvailableBytes = (long)(512L * 1024 * 1024 * 1024 * 0.37) },
                new() { MountPoint = "/home", FileSystem = "/dev/nvme0n1p3", TotalBytes = 1024L * 1024 * 1024 * 1024, UsedBytes = (long)(1024L * 1024 * 1024 * 1024 * 0.41), AvailableBytes = (long)(1024L * 1024 * 1024 * 1024 * 0.59) },
                new() { MountPoint = "/data", FileSystem = "/dev/sdb1", TotalBytes = 4096L * 1024 * 1024 * 1024, UsedBytes = (long)(4096L * 1024 * 1024 * 1024 * 0.78), AvailableBytes = (long)(4096L * 1024 * 1024 * 1024 * 0.22) },
            },
            Networks = new List<NetworkSample>
            {
                new()
                {
                    InterfaceName = "eth0",
                    BytesReceived = (long)(elapsed * 1024 * 1024 * 1.8),
                    BytesSent = (long)(elapsed * 1024 * 1024 * 0.9),
                    PacketsReceived = (long)(elapsed * 900),
                    PacketsSent = (long)(elapsed * 600),
                },
                new()
                {
                    InterfaceName = "wlan0",
                    BytesReceived = (long)(elapsed * 1024 * 1024 * 0.3),
                    BytesSent = (long)(elapsed * 1024 * 1024 * 0.1),
                    PacketsReceived = (long)(elapsed * 200),
                    PacketsSent = (long)(elapsed * 120),
                },
            },
            DiskReadBytesPerSecond = Math.Max(0, (6 * 1024 * 1024) + (wave * 2 * 1024 * 1024)),
            DiskWriteBytesPerSecond = Math.Max(0, (2 * 1024 * 1024) + (wave2 * 1024 * 1024)),
            Uptime = TimeSpan.FromDays(12) + TimeSpan.FromMinutes(43),
        };

        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken)
    {
        var processes = new List<RemoteProcessInfo>
        {
            new() { ProcessId = 1411, Name = "surge-agent", User = "demo", CpuPercent = 12.4, MemoryPercent = 3.1, ResidentBytes = 512L * 1024 * 1024, CommandLine = "/usr/bin/surge-agent --daemon" },
            new() { ProcessId = 2044, Name = "dotnet", User = "demo", CpuPercent = 8.7, MemoryPercent = 5.4, ResidentBytes = 890L * 1024 * 1024, CommandLine = "dotnet run --project SurgeLink.App" },
            new() { ProcessId = 812, Name = "systemd", User = "root", CpuPercent = 1.2, MemoryPercent = 0.4, ResidentBytes = 68L * 1024 * 1024, CommandLine = "/sbin/init splash" },
            new() { ProcessId = 1330, Name = "gnome-shell", User = "demo", CpuPercent = 3.9, MemoryPercent = 2.8, ResidentBytes = 460L * 1024 * 1024, CommandLine = "/usr/bin/gnome-shell" },
            new() { ProcessId = 1522, Name = "firefox", User = "demo", CpuPercent = 6.1, MemoryPercent = 7.2, ResidentBytes = 1180L * 1024 * 1024, CommandLine = "/usr/lib/firefox/firefox" },
            new() { ProcessId = 3011, Name = "postgres", User = "postgres", CpuPercent = 2.3, MemoryPercent = 1.9, ResidentBytes = 310L * 1024 * 1024, CommandLine = "postgres: checkpointer" },
            new() { ProcessId = 98, Name = "kworker/0:2", User = "root", CpuPercent = 0.4, MemoryPercent = 0.0, ResidentBytes = 0, CommandLine = "[kworker/0:2]" },
        };

        return Task.FromResult<IReadOnlyList<RemoteProcessInfo>>(processes.Take(Math.Max(1, maxCount)).ToList());
    }
}
