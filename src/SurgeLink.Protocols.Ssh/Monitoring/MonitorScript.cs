namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Builds the single POSIX shell script used to collect every counter of one
/// <see cref="SurgeLink.Core.Monitoring.SystemSnapshot"/> in one round trip.
/// </summary>
public static class MonitorScript
{
    /// <summary>Section name of the <c>/proc/stat</c> contents.</summary>
    public const string StatSection = "stat";

    /// <summary>Section name of the <c>/proc/meminfo</c> contents.</summary>
    public const string MemInfoSection = "meminfo";

    /// <summary>Section name of the <c>/proc/loadavg</c> contents.</summary>
    public const string LoadAvgSection = "loadavg";

    /// <summary>Section name of the <c>/proc/uptime</c> contents.</summary>
    public const string UptimeSection = "uptime";

    /// <summary>Section name of the <c>/proc/net/dev</c> contents.</summary>
    public const string NetDevSection = "netdev";

    /// <summary>Section name of the <c>/proc/diskstats</c> contents.</summary>
    public const string DiskStatsSection = "diskstats";

    /// <summary>Section name of the <c>df -kP</c> output.</summary>
    public const string DiskUsageSection = "df";

    /// <summary>The complete script; missing files simply leave an empty section behind.</summary>
    public static string Command { get; } = BuildCommand();

    /// <summary>Builds the script; exposed for tests that assert on the collected commands.</summary>
    public static string BuildCommand()
    {
        var lines = new[]
        {
            Mark(StatSection),
            "cat /proc/stat 2>/dev/null",
            Mark(MemInfoSection),
            "cat /proc/meminfo 2>/dev/null",
            Mark(LoadAvgSection),
            "cat /proc/loadavg 2>/dev/null",
            Mark(UptimeSection),
            "cat /proc/uptime 2>/dev/null",
            Mark(NetDevSection),
            "cat /proc/net/dev 2>/dev/null",
            Mark(DiskStatsSection),
            "cat /proc/diskstats 2>/dev/null",
            Mark(DiskUsageSection),
            "LC_ALL=C df -kP 2>/dev/null",
        };

        return string.Join('\n', lines);
    }

    private static string Mark(string section) => "printf '%s\\n' '" + MonitorOutput.MarkerPrefix + section + "'";
}
