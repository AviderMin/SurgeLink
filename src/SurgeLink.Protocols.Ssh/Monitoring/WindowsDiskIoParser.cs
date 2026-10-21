using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Aggregate disk throughput reported by one Windows performance sample.</summary>
public sealed record DiskIoRates(double ReadBytesPerSecond, double WriteBytesPerSecond);

/// <summary>Parses the logical disk I/O section of a Windows monitor sample.</summary>
public static class WindowsDiskIoParser
{
    /// <summary>Parses rows shaped <c>name|readBytesPerSecond|writeBytesPerSecond</c>.</summary>
    /// <param name="section">Raw section text; malformed rows are skipped.</param>
    /// <returns>Aggregate rates across all logical disks; zero when nothing usable was reported.</returns>
    public static DiskIoRates Parse(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return new DiskIoRates(0, 0);
        }

        double read = 0;
        double write = 0;
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 3
                || !TryParseRate(fields[1], out var lineRead)
                || !TryParseRate(fields[2], out var lineWrite))
            {
                continue;
            }

            read += lineRead;
            write += lineWrite;
        }

        return new DiskIoRates(read, write);
    }

    private static bool TryParseRate(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
}
