using System.Globalization;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>CPU utilisation of a Windows guest parsed from the monitor sections.</summary>
public sealed record WindowsCpuSample
{
    /// <summary>A sample with no data.</summary>
    public static WindowsCpuSample Empty { get; } = new();

    /// <summary>Total utilisation across all logical processors, 0..100.</summary>
    public double TotalPercent { get; init; }

    /// <summary>Per logical processor utilisation, 0..100.</summary>
    public IReadOnlyList<double> PerCorePercent { get; init; } = Array.Empty<double>();

    /// <summary>Number of logical processors reported by the guest, or zero when unknown.</summary>
    public int LogicalProcessorCount { get; init; }
}

/// <summary>Parses the CPU sections of a Windows monitor sample.</summary>
public static class WindowsCpuParser
{
    /// <summary>
    /// Parses the perf formatted per processor section and the <c>Win32_Processor</c>
    /// fallback section. Missing or malformed lines degrade to zero.
    /// </summary>
    /// <param name="cpuSection">Per processor rows shaped <c>name|percent</c>.</param>
    /// <param name="processorSection">Rows shaped <c>loadPercentage|logicalProcessors|cores</c>.</param>
    public static WindowsCpuSample Parse(string? cpuSection, string? processorSection)
    {
        double? total = null;
        var cores = new SortedDictionary<int, double>();
        var maximumCoreIndex = -1;

        if (!string.IsNullOrWhiteSpace(cpuSection))
        {
            foreach (var rawLine in cpuSection.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var fields = line.Split('|');
                if (fields.Length < 2 || !TryParsePercent(fields[1], out var percent))
                {
                    continue;
                }

                var name = fields[0].Trim();
                if (string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase))
                {
                    total ??= percent;
                    continue;
                }

                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0)
                {
                    cores[index] = percent;
                    if (index > maximumCoreIndex)
                    {
                        maximumCoreIndex = index;
                    }
                }
            }
        }

        var loads = new List<double>();
        var logicalProcessors = 0;
        if (!string.IsNullOrWhiteSpace(processorSection))
        {
            foreach (var rawLine in processorSection.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var fields = line.Split('|');
                if (fields.Length < 2 || !TryParsePercent(fields[0], out var load))
                {
                    continue;
                }

                loads.Add(load);
                if (int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
                {
                    logicalProcessors += count;
                }
            }
        }

        if (total is null && loads.Count > 0)
        {
            var sum = 0d;
            foreach (var load in loads)
            {
                sum += load;
            }

            total = sum / loads.Count;
        }

        var perCore = new double[maximumCoreIndex + 1];
        foreach (var pair in cores)
        {
            perCore[pair.Key] = pair.Value;
        }

        if (total is null && perCore.Length > 0)
        {
            var sum = 0d;
            foreach (var value in perCore)
            {
                sum += value;
            }

            total = sum / perCore.Length;
        }

        if (logicalProcessors <= 0 && perCore.Length > 0)
        {
            logicalProcessors = perCore.Length;
        }

        if (total is null)
        {
            return WindowsCpuSample.Empty;
        }

        return new WindowsCpuSample
        {
            TotalPercent = total.Value,
            PerCorePercent = perCore,
            LogicalProcessorCount = logicalProcessors,
        };
    }

    private static bool TryParsePercent(string text, out double value)
    {
        value = 0;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed))
        {
            return false;
        }

        value = Math.Clamp(parsed, 0d, 100d);
        return true;
    }
}
