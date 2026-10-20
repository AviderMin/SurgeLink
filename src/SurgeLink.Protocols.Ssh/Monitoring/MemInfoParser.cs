using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses <c>/proc/meminfo</c> into a <see cref="MemorySample"/>.</summary>
public static class MemInfoParser
{
    private const long BytesPerKilobyte = 1024;

    /// <summary>Parses the <c>Key: value kB</c> lines of a <c>/proc/meminfo</c> snapshot.</summary>
    /// <param name="text">Raw <c>/proc/meminfo</c> contents; may be <c>null</c> or truncated.</param>
    /// <returns>A memory sample; fields that could not be read are reported as zero.</returns>
    public static MemorySample Parse(string? text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon].Trim();
                var rest = line[(colon + 1)..].Trim();
                var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    continue;
                }

                if (!long.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
                {
                    continue;
                }

                var multiplier = tokens.Length > 1 && tokens[1].Equals("kB", StringComparison.OrdinalIgnoreCase)
                    ? BytesPerKilobyte
                    : 1L;
                values[key] = value * multiplier;
            }
        }

        var total = Get(values, "MemTotal");
        var free = Get(values, "MemFree");
        var buffers = Get(values, "Buffers");
        var cached = Get(values, "Cached");
        var available = Get(values, "MemAvailable");
        if (available <= 0)
        {
            available = free + buffers + cached;
        }

        var swapTotal = Get(values, "SwapTotal");
        var swapFree = Get(values, "SwapFree");

        return new MemorySample
        {
            TotalBytes = Math.Max(0, total),
            AvailableBytes = Math.Clamp(available, 0, Math.Max(0, total)),
            CachedBytes = Math.Max(0, cached),
            SwapTotalBytes = Math.Max(0, swapTotal),
            SwapUsedBytes = Math.Max(0, swapTotal - swapFree),
        };
    }

    private static long Get(Dictionary<string, long> values, string key) =>
        values.TryGetValue(key, out var value) ? value : 0L;
}
