using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses the logical disk section of a Windows monitor sample.</summary>
public static class WindowsDiskParser
{
    /// <summary>Parses rows shaped <c>device|sizeBytes|freeBytes|fileSystem</c>.</summary>
    /// <param name="diskSection">Raw section text; malformed rows are skipped.</param>
    /// <returns>One <see cref="DiskSample"/> per readable drive.</returns>
    public static IReadOnlyList<DiskSample> Parse(string? diskSection)
    {
        if (string.IsNullOrWhiteSpace(diskSection))
        {
            return Array.Empty<DiskSample>();
        }

        var disks = new List<DiskSample>();
        foreach (var rawLine in diskSection.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 3)
            {
                continue;
            }

            var device = fields[0].Trim();
            if (device.Length == 0 ||
                !TryParseBytes(fields[1], out var total) ||
                !TryParseBytes(fields[2], out var free) ||
                total <= 0)
            {
                continue;
            }

            var available = Math.Min(free, total);
            var fileSystem = fields.Length > 3 ? fields[3].Trim() : string.Empty;
            disks.Add(new DiskSample
            {
                MountPoint = device,
                FileSystem = fileSystem.Length == 0 ? null : fileSystem,
                TotalBytes = total,
                UsedBytes = total - available,
                AvailableBytes = available,
            });
        }

        return disks;
    }

    private static bool TryParseBytes(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
}
