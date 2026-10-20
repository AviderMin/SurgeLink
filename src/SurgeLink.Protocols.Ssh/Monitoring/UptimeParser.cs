using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses <c>/proc/uptime</c>.</summary>
public static class UptimeParser
{
    /// <summary>Parses the uptime in seconds from the first field of <c>/proc/uptime</c>.</summary>
    /// <param name="text">Raw <c>/proc/uptime</c> contents; may be <c>null</c> or truncated.</param>
    /// <returns>The uptime, or <c>null</c> when no valid value could be parsed.</returns>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tokens = text.Split('\n')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            double.IsNaN(seconds) ||
            seconds < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2d));
    }
}
