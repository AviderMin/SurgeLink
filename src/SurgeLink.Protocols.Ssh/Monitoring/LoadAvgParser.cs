using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>One parsed <c>/proc/loadavg</c> line.</summary>
/// <param name="Load1">One minute load average.</param>
/// <param name="Load5">Five minute load average.</param>
/// <param name="Load15">Fifteen minute load average.</param>
/// <param name="RunnableCount">Currently runnable scheduling entities.</param>
/// <param name="TotalThreads">Total scheduling entities on the host.</param>
public sealed record LoadAverageSample(double Load1, double Load5, double Load15, int RunnableCount, int TotalThreads);

/// <summary>Parses <c>/proc/loadavg</c>.</summary>
public static class LoadAvgParser
{
    /// <summary>Parses the first line of a <c>/proc/loadavg</c> snapshot.</summary>
    /// <param name="text">Raw <c>/proc/loadavg</c> contents; may be <c>null</c> or truncated.</param>
    /// <returns>The parsed sample, or <c>null</c> when the three load values are missing.</returns>
    public static LoadAverageSample? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n')[0].Trim();
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return null;
        }

        if (!TryParseDouble(tokens[0], out var load1) ||
            !TryParseDouble(tokens[1], out var load5) ||
            !TryParseDouble(tokens[2], out var load15))
        {
            return null;
        }

        var runnable = 0;
        var threads = 0;
        if (tokens.Length > 3)
        {
            var parts = tokens[3].Split('/');
            if (parts.Length == 2)
            {
                _ = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out runnable);
                _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out threads);
            }
        }

        return new LoadAverageSample(load1, load5, load15, Math.Max(0, runnable), Math.Max(0, threads));
    }

    private static bool TryParseDouble(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && value >= 0;
}
