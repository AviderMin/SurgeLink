using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Parses the textual output of common <c>ps</c> variants (<c>ps -eo</c>, <c>ps aux</c>,
/// procps and busybox) into process rows. Rows that cannot be understood are skipped.
/// </summary>
public static class PsParser
{
    /// <summary>Upper bound applied when trimming a process list.</summary>
    public const int MaximumProcessCount = 500;

    private const double MaximumReportedPercent = 10_000d;

    private const long BytesPerKilobyte = 1024;

    private static readonly string[] CommandColumns = { "COMMAND", "CMD", "COMM", "ARGS", "NAME" };
    private static readonly string[] CpuColumns = { "%CPU", "PCPU", "C" };
    private static readonly string[] MemoryColumns = { "%MEM", "PMEM" };
    private static readonly string[] UserColumns = { "USER", "USERNAME", "UID" };

    /// <summary>Parses complete <c>ps</c> output, detecting the column layout from the header line.</summary>
    /// <param name="text">Raw <c>ps</c> output; may be <c>null</c>, empty or truncated.</param>
    /// <returns>Parsed rows in output order, or an empty list when nothing could be understood.</returns>
    public static IReadOnlyList<RemoteProcessInfo> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<RemoteProcessInfo>();
        }

        var lines = text.Split('\n');
        var headerIndex = -1;
        string[]? header = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var candidate = lines[i].Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            var tokens = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (IsHeader(tokens))
            {
                headerIndex = i;
                header = tokens;
            }

            break;
        }

        return header is null
            ? ParseHeaderless(lines)
            : ParseWithHeader(lines, headerIndex, header);
    }

    /// <summary>Orders rows by CPU usage descending and caps the list.</summary>
    /// <param name="processes">Rows to order.</param>
    /// <param name="maxCount">Maximum number of rows; values below one produce an empty list.</param>
    public static IReadOnlyList<RemoteProcessInfo> TopByCpu(IEnumerable<RemoteProcessInfo> processes, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (maxCount <= 0)
        {
            return Array.Empty<RemoteProcessInfo>();
        }

        var limit = Math.Min(maxCount, MaximumProcessCount);
        return processes
            .OrderByDescending(process => process.CpuPercent)
            .ThenBy(process => process.ProcessId)
            .Take(limit)
            .ToList();
    }

    private static bool IsHeader(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return false;
        }

        var hasProcessId = Contains(tokens, "PID");
        if (!hasProcessId && !Contains(tokens, "UID"))
        {
            return false;
        }

        return ContainsAny(tokens, CommandColumns);
    }

    private static IReadOnlyList<RemoteProcessInfo> ParseWithHeader(string[] lines, int headerIndex, string[] header)
    {
        var map = MapColumns(header);
        if (map.ProcessId < 0)
        {
            return Array.Empty<RemoteProcessInfo>();
        }

        var results = new List<RemoteProcessInfo>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = SplitWithLimit(line, header.Length);
            if (tokens.Length < Math.Min(3, header.Length))
            {
                continue;
            }

            if (!int.TryParse(ValueAt(tokens, map.ProcessId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                processId < 0)
            {
                continue;
            }

            var user = ValueAt(tokens, map.User);
            var command = ValueAt(tokens, map.Command);
            results.Add(new RemoteProcessInfo
            {
                ProcessId = processId,
                Name = DeriveName(command),
                User = user.Length == 0 ? null : user,
                CpuPercent = ParsePercent(ValueAt(tokens, map.Cpu)),
                MemoryPercent = ParsePercent(ValueAt(tokens, map.Memory)),
                ResidentBytes = Math.Max(0, ParseLong(ValueAt(tokens, map.Resident))) * BytesPerKilobyte,
                CommandLine = command.Length == 0 ? null : command,
            });
        }

        return results;
    }

    private static IReadOnlyList<RemoteProcessInfo> ParseHeaderless(string[] lines)
    {
        var results = new List<RemoteProcessInfo>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) || processId < 0)
            {
                continue;
            }

            var commandStart = 2;
            if (tokens.Length > 4 && long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                commandStart = 4;
            }
            else if (tokens.Length > 3 && IsTimeColumn(tokens[2]))
            {
                commandStart = 3;
            }

            var command = string.Join(' ', tokens[commandStart..]);
            results.Add(new RemoteProcessInfo
            {
                ProcessId = processId,
                Name = DeriveName(command),
                User = tokens[1],
                CommandLine = command.Length == 0 ? null : command,
            });
        }

        return results;
    }

    private static ColumnMap MapColumns(string[] header)
    {
        var map = new ColumnMap(-1, -1, -1, -1, -1, -1);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim().ToUpperInvariant();
            if (map.ProcessId < 0 && string.Equals(name, "PID", StringComparison.Ordinal))
            {
                map = map with { ProcessId = i };
            }
            else if (map.User < 0 && Contains(UserColumns, name))
            {
                map = map with { User = i };
            }
            else if (map.Cpu < 0 && Contains(CpuColumns, name))
            {
                map = map with { Cpu = i };
            }
            else if (map.Memory < 0 && Contains(MemoryColumns, name))
            {
                map = map with { Memory = i };
            }
            else if (map.Resident < 0 && string.Equals(name, "RSS", StringComparison.Ordinal))
            {
                map = map with { Resident = i };
            }
            else if (Contains(CommandColumns, name))
            {
                map = map with { Command = i };
            }
        }

        return map;
    }

    private static string[] SplitWithLimit(string line, int limit)
    {
        var tokens = new List<string>(Math.Min(limit, 16));
        var index = 0;
        while (index < line.Length && tokens.Count < limit - 1)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            if (index >= line.Length)
            {
                break;
            }

            var start = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            tokens.Add(line[start..index]);
        }

        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        if (index < line.Length && tokens.Count < limit)
        {
            tokens.Add(line[index..].TrimEnd());
        }

        return tokens.ToArray();
    }

    private static string ValueAt(string[] tokens, int index) =>
        index >= 0 && index < tokens.Length ? tokens[index] : string.Empty;

    private static double ParsePercent(string value)
    {
        var trimmed = value.TrimEnd('%');
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            double.IsNaN(parsed) || parsed < 0)
        {
            return 0d;
        }

        return Math.Min(parsed, MaximumReportedPercent);
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;

    private static string DeriveName(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var text = command.Trim();
        if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
        {
            return text[1..^1];
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var executable = space > 0 ? text[..space] : text;
        var slash = executable.LastIndexOf('/');
        return slash >= 0 && slash + 1 < executable.Length ? executable[(slash + 1)..] : executable;
    }

    private static bool IsTimeColumn(string token)
    {
        if (!token.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiDigit(character) && character != ':' && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string[] tokens, string value)
    {
        foreach (var token in tokens)
        {
            if (string.Equals(token, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string[] tokens, string[] values)
    {
        foreach (var value in values)
        {
            if (Contains(tokens, value))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct ColumnMap(int ProcessId, int User, int Cpu, int Memory, int Resident, int Command);
}