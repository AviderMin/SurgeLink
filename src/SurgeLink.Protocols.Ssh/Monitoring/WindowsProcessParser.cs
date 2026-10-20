using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses the top process rows of a Windows guest.</summary>
public static class WindowsProcessParser
{
    private const double MaximumReportedPercent = 10_000d;

    /// <summary>
    /// Parses rows shaped <c>id|name|percent|workingSetBytes|threads</c> produced by either the
    /// perf formatted data command or the <c>Get-Process</c> fallback.
    /// </summary>
    /// <param name="text">Raw command output; malformed rows are skipped.</param>
    /// <returns>Process rows in the order emitted by the guest.</returns>
    public static IReadOnlyList<RemoteProcessInfo> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<RemoteProcessInfo>();
        }

        var processes = new List<RemoteProcessInfo>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 4 ||
                !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                processId <= 0)
            {
                continue;
            }

            var name = NormalizeName(fields[1]);
            if (name.Length == 0 ||
                string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            processes.Add(new RemoteProcessInfo
            {
                ProcessId = processId,
                Name = name,
                CpuPercent = Math.Clamp(TryParseDouble(fields[2]), 0d, MaximumReportedPercent),
                ResidentBytes = TryParseLong(fields[3]),
                User = null,
                CommandLine = null,
            });
        }

        return processes;
    }

    private static string NormalizeName(string raw)
    {
        var name = raw.Trim();
        var hash = name.LastIndexOf('#');
        if (hash > 0 && hash + 1 < name.Length &&
            int.TryParse(name[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            name = name[..hash];
        }

        return name;
    }

    private static double TryParseDouble(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value
            : 0d;

    private static long TryParseLong(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : 0L;
}
