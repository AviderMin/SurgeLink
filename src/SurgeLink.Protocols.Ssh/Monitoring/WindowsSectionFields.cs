using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Reads <c>key|value</c> lines emitted by <see cref="WindowsMonitorScript"/>.</summary>
internal static class WindowsSectionFields
{
    /// <summary>Parses a section into a case insensitive field map; malformed lines are skipped.</summary>
    public static Dictionary<string, string> Parse(string? section)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(section))
        {
            return fields;
        }

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            fields[key] = line[(separator + 1)..].Trim();
        }

        return fields;
    }

    /// <summary>Reads a non negative integer count; missing or malformed values are zero.</summary>
    public static int GetCount(Dictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
               value >= 0
            ? value
            : 0;
    }

    /// <summary>Reads a non negative 64 bit value.</summary>
    public static bool TryGetLong(Dictionary<string, string> fields, string key, out long value)
    {
        value = 0;
        return fields.TryGetValue(key, out var text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
               value >= 0;
    }
}
