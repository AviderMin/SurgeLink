using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>One device line of <c>/proc/diskstats</c>.</summary>
public sealed record DiskIoStat
{
    /// <summary>Kernel device name, for example <c>sda</c> or <c>nvme0n1p1</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Number of completed reads.</summary>
    public long ReadsCompleted { get; init; }

    /// <summary>Number of sectors read (512 byte sectors).</summary>
    public long SectorsRead { get; init; }

    /// <summary>Number of completed writes.</summary>
    public long WritesCompleted { get; init; }

    /// <summary>Number of sectors written (512 byte sectors).</summary>
    public long SectorsWritten { get; init; }

    /// <summary>I/O operations currently in progress.</summary>
    public long IosInProgress { get; init; }

    /// <summary>Milliseconds spent doing I/O.</summary>
    public long IoMilliseconds { get; init; }
}

/// <summary>Parses <c>/proc/diskstats</c>; per device detail is not part of the snapshot contract.</summary>
public static class DiskStatsParser
{
    private const int MinimumFieldCount = 10;

    /// <summary>Parses the device lines of a <c>/proc/diskstats</c> snapshot.</summary>
    /// <param name="text">Raw <c>/proc/diskstats</c> contents; may be <c>null</c> or truncated.</param>
    /// <returns>One entry per device line that could be parsed.</returns>
    public static IReadOnlyList<DiskIoStat> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DiskIoStat>();
        }

        var stats = new List<DiskIoStat>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < MinimumFieldCount)
            {
                continue;
            }

            if (!TryParseLong(tokens[0], out _) || !TryParseLong(tokens[1], out _))
            {
                continue;
            }

            var deviceName = tokens[2];
            if (deviceName.Length == 0)
            {
                continue;
            }

            if (!TryParseLong(tokens[3], out var readsCompleted) ||
                !TryParseLong(tokens[5], out var sectorsRead) ||
                !TryParseLong(tokens[7], out var writesCompleted) ||
                !TryParseLong(tokens[9], out var sectorsWritten))
            {
                continue;
            }

            _ = TryParseLong(tokens.Length > 11 ? tokens[11] : string.Empty, out var inProgress);
            _ = TryParseLong(tokens.Length > 12 ? tokens[12] : string.Empty, out var milliseconds);

            stats.Add(new DiskIoStat
            {
                DeviceName = deviceName,
                ReadsCompleted = readsCompleted,
                SectorsRead = sectorsRead,
                WritesCompleted = writesCompleted,
                SectorsWritten = sectorsWritten,
                IosInProgress = inProgress,
                IoMilliseconds = milliseconds,
            });
        }

        return stats;
    }

    private static bool TryParseLong(string token, out long value) =>
        long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
}
