using System.Text;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Splits the delimited output of <see cref="MonitorScript"/> into named sections.</summary>
public static class MonitorOutput
{
    /// <summary>Prefix of the marker line that introduces each section.</summary>
    public const string MarkerPrefix = "@@@SURGELINK:";

    /// <summary>Splits raw command output into sections keyed by marker name.</summary>
    /// <param name="output">Raw standard output of the monitor script; may be <c>null</c> or truncated.</param>
    /// <returns>Section contents with line endings preserved except for the trailing newline.</returns>
    public static IReadOnlyDictionary<string, string> Split(string? output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output))
        {
            return sections;
        }

        var lines = output.Split('\n');
        string? current = null;
        var builder = new StringBuilder();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var candidate = line.TrimStart();
            if (candidate.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections[current] = builder.ToString();
                }

                current = candidate[MarkerPrefix.Length..].Trim();
                builder.Clear();
                continue;
            }

            if (current is null)
            {
                continue;
            }

            // The final empty element is an artifact of the command output ending with a newline.
            if (index == lines.Length - 1 && line.Length == 0)
            {
                continue;
            }

            builder.Append(line).Append('\n');
        }

        if (current is not null)
        {
            sections[current] = builder.ToString();
        }

        return sections;
    }
}