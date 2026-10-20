using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses the CPU lines of <c>/proc/stat</c>, tolerating 2.6 era short lines.</summary>
public static class ProcStatParser
{
    /// <summary>Parses the <c>cpu</c> and <c>cpuN</c> lines of a <c>/proc/stat</c> snapshot.</summary>
    /// <param name="text">Raw <c>/proc/stat</c> contents; may be <c>null</c> or truncated.</param>
    /// <returns>The parsed sample, or <c>null</c> when no CPU line could be read.</returns>
    public static ProcStatSample? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ProcCpuTimes? total = null;
        var cores = new List<ProcCpuTimes>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            if (!TryParseCounters(tokens, out var counters))
            {
                continue;
            }

            if (tokens[0].Length == 3)
            {
                total ??= counters;
                continue;
            }

            if (!TryParseCoreIndex(tokens[0], out var coreIndex))
            {
                continue;
            }

            while (cores.Count <= coreIndex)
            {
                cores.Add(default);
            }

            cores[coreIndex] = counters;
        }

        return total is null
            ? null
            : new ProcStatSample { Total = total.Value, PerCore = cores };
    }

    private static bool TryParseCounters(string[] tokens, out ProcCpuTimes counters)
    {
        counters = default;
        Span<long> values = stackalloc long[10];
        for (var i = 1; i < tokens.Length && i <= values.Length; i++)
        {
            if (!long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                return false;
            }

            values[i - 1] = value;
        }

        counters = new ProcCpuTimes(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5],
            values[6],
            values[7],
            values[8],
            values[9]);
        return true;
    }

    private static bool TryParseCoreIndex(string name, out int coreIndex)
    {
        coreIndex = -1;
        if (name.Length <= 3)
        {
            return false;
        }

        return int.TryParse(name.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out coreIndex) && coreIndex >= 0;
    }
}
