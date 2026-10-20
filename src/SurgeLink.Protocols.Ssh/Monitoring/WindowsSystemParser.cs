using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Process, thread and uptime counters of a Windows guest.</summary>
public sealed record WindowsSystemSample
{
    /// <summary>A sample with no data.</summary>
    public static WindowsSystemSample Empty { get; } = new();

    /// <summary>Number of processes reported by the guest.</summary>
    public int ProcessCount { get; init; }

    /// <summary>Number of threads reported by the guest.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Time since the guest booted, when the boot time could be read.</summary>
    public TimeSpan? Uptime { get; init; }
}

/// <summary>Parses the system section of a Windows monitor sample.</summary>
public static class WindowsSystemParser
{
    /// <summary>Parses <c>processes</c>, <c>threads</c> and <c>boot</c> (ISO 8601) fields.</summary>
    /// <param name="systemSection">Rows shaped <c>key|value</c>.</param>
    /// <param name="now">Current time used to derive the uptime; injected for testability.</param>
    public static WindowsSystemSample Parse(string? systemSection, DateTimeOffset now)
    {
        var fields = WindowsSectionFields.Parse(systemSection);
        var processCount = WindowsSectionFields.GetCount(fields, "processes");
        var threadCount = WindowsSectionFields.GetCount(fields, "threads");

        TimeSpan? uptime = null;
        if (fields.TryGetValue("boot", out var bootText) &&
            DateTimeOffset.TryParse(
                bootText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var bootTime))
        {
            var delta = now - bootTime;
            if (delta >= TimeSpan.Zero)
            {
                uptime = delta;
            }
        }

        return new WindowsSystemSample
        {
            ProcessCount = processCount,
            ThreadCount = threadCount,
            Uptime = uptime,
        };
    }
}
