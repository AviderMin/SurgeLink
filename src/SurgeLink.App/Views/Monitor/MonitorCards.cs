using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Styling;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Monitor;

/// <summary>
/// The dashboard cards. Each builder returns a <see cref="MetricCard"/> and
/// degrades to a "n/a" caption when the protocol could not report that section,
/// which is what servers without <c>/proc</c> look like.
/// </summary>
internal static class MonitorCards
{
    private static Element Card(string title, string? caption, Element content) =>
        Component<MetricCard, MetricCardProps>(new MetricCardProps(title, caption, content));

    /// <summary>CPU gauge, per core bars and a rolling utilisation chart.</summary>
    public static Element Cpu(SystemSnapshot snapshot, MonitorHistory history)
    {
        var cpu = snapshot.Cpu;
        var cores = cpu.PerCorePercent;
        var caption = cores.Count > 0
            ? DataFormat.PercentValue(cpu.TotalPercent) + " average · " + cores.Count + " logical cores"
            : DataFormat.PercentValue(cpu.TotalPercent) + " average · per core data not available";

        Element detail;
        if (cores.Count == 0)
        {
            detail = Caption("This host reports only the total CPU utilisation.").Foreground(Theme.SecondaryText);
        }
        else
        {
            var perCore = new List<Element>(cores.Count);
            for (var index = 0; index < cores.Count; index++)
            {
                perCore.Add(
                    Component<UsageBar, UsageBarProps>(new UsageBarProps(
                            cores[index] / 100d,
                            "CPU " + index,
                            DataFormat.Percent(cores[index] / 100d)))
                        .Grid(row: index / 2, column: index % 2));
            }

            detail = Grid(
                columns: [GridSize.Star(), GridSize.Star()],
                rows: Enumerable.Repeat(GridSize.Auto, (cores.Count + 1) / 2).ToArray(),
                perCore.ToArray());
        }

        var body = VStack(
            AppTheme.SpacingM,
            Grid(
                columns: [GridSize.Auto, GridSize.Star()],
                rows: [GridSize.Auto],
                Component<Gauge, GaugeProps>(new GaugeProps(
                        cpu.TotalPercent / 100d,
                        DataFormat.PercentValue(cpu.TotalPercent),
                        "total",
                        112))
                    .Grid(row: 0, column: 0),
                detail.Grid(row: 0, column: 1).Margin(AppTheme.SpacingL, 0, 0, 0)),
            Component<LineChart, LineChartProps>(new LineChartProps(
                    [new ChartSeries("CPU", history.CpuHistory)],
                    Height: 64,
                    MaxValue: 100))
                .AutomationName("CPU history"));

        return Card("CPU", caption, body);
    }

    /// <summary>Memory and swap usage.</summary>
    public static Element Memory(SystemSnapshot snapshot)
    {
        var memory = snapshot.Memory;
        if (memory.TotalBytes <= 0)
        {
            return Card("Memory", null, Caption("Memory counters are not available on this host.").Foreground(Theme.SecondaryText));
        }

        var swapFraction = memory.SwapTotalBytes > 0
            ? Math.Clamp((double)memory.SwapUsedBytes / memory.SwapTotalBytes, 0d, 1d)
            : 0d;

        var bars = VStack(
            AppTheme.SpacingM,
            Component<UsageBar, UsageBarProps>(new UsageBarProps(
                memory.UsedFraction,
                "Physical",
                DataFormat.Percent(memory.UsedFraction) + " of " + DataFormat.Bytes(memory.TotalBytes))),
            memory.SwapTotalBytes > 0
                ? Component<UsageBar, UsageBarProps>(new UsageBarProps(
                    swapFraction,
                    "Swap",
                    DataFormat.Percent(swapFraction) + " of " + DataFormat.Bytes(memory.SwapTotalBytes)))
                : Component<UsageBar, UsageBarProps>(new UsageBarProps(0, "Swap", "not configured")),
            Caption("Cached " + DataFormat.Bytes(memory.CachedBytes) + " · available "
                    + DataFormat.Bytes(memory.AvailableBytes)).Foreground(Theme.SecondaryText));

        var body = Grid(
            columns: [GridSize.Auto, GridSize.Star()],
            rows: [GridSize.Auto],
            Component<Gauge, GaugeProps>(new GaugeProps(
                    memory.UsedFraction,
                    DataFormat.Percent(memory.UsedFraction),
                    DataFormat.Bytes(memory.UsedBytes),
                    112))
                .Grid(row: 0, column: 0),
            bars.Grid(row: 0, column: 1).Margin(AppTheme.SpacingL, 0, 0, 0));

        return Card(
            "Memory",
            DataFormat.Bytes(memory.UsedBytes) + " of " + DataFormat.Bytes(memory.TotalBytes) + " in use",
            body);
    }

    /// <summary>Network throughput with a two series rolling chart.</summary>
    public static Element Network(SystemSnapshot snapshot, MonitorHistory history)
    {
        if (snapshot.Networks.Count == 0)
        {
            return Card("Network", null, Caption("Network counters are not available on this host.").Foreground(Theme.SecondaryText));
        }

        var interfaces = string.Join(" · ", snapshot.Networks.Select(network => network.InterfaceName));
        var body = VStack(
            AppTheme.SpacingM,
            Grid(
                columns: [GridSize.Star(), GridSize.Star()],
                rows: [GridSize.Auto],
                Stat("Download", DataFormat.Rate(history.ReceiveRate)).Grid(row: 0, column: 0),
                Stat("Upload", DataFormat.Rate(history.SendRate)).Grid(row: 0, column: 1)),
            Component<LineChart, LineChartProps>(new LineChartProps(
                    [
                        new ChartSeries("Download", history.ReceiveHistory),
                        new ChartSeries("Upload", history.SendHistory),
                    ],
                    Height: 88))
                .AutomationName("Network throughput history"),
            Caption("Interfaces: " + interfaces).Foreground(Theme.SecondaryText));

        return Card("Network", "Throughput over the last " + MonitorHistory.Capacity + " samples", body);
    }

    /// <summary>Load averages, uptime and process/thread counts.</summary>
    public static Element Host(SystemSnapshot snapshot)
    {
        var cpu = snapshot.Cpu;
        var hasLoad = cpu.Load1 > 0 || cpu.Load5 > 0 || cpu.Load15 > 0 || cpu.ProcessCount > 0;
        var body = Grid(
            columns: [GridSize.Star(), GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto],
            Stat("Load 1m", hasLoad ? cpu.Load1.ToString("0.00") : "n/a").Grid(row: 0, column: 0),
            Stat("Load 5m", hasLoad ? cpu.Load5.ToString("0.00") : "n/a").Grid(row: 0, column: 1),
            Stat("Load 15m", hasLoad ? cpu.Load15.ToString("0.00") : "n/a").Grid(row: 0, column: 2),
            Stat("Uptime", snapshot.Uptime is { } uptime ? DataFormat.Duration(uptime) : "n/a").Grid(row: 1, column: 0),
            Stat("Processes", cpu.ProcessCount > 0 ? DataFormat.Count(cpu.ProcessCount) : "n/a").Grid(row: 1, column: 1),
            Stat("Threads", cpu.ThreadCount > 0 ? DataFormat.Count(cpu.ThreadCount) : "n/a").Grid(row: 1, column: 2));

        return Card("System", snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), body);
    }

    /// <summary>Disk usage bars, one per mount point.</summary>
    public static Element Disks(SystemSnapshot snapshot)
    {
        if (snapshot.Disks.Count == 0)
        {
            return Card("Disks", null, Caption("No mounted file systems were reported.").Foreground(Theme.SecondaryText));
        }

        var bars = new List<Element>(snapshot.Disks.Count);
        foreach (var disk in snapshot.Disks)
        {
            bars.Add(Component<UsageBar, UsageBarProps>(new UsageBarProps(
                disk.UsedFraction,
                disk.MountPoint,
                DataFormat.Percent(disk.UsedFraction) + " of " + DataFormat.Bytes(disk.TotalBytes) + " used")));
        }

        return Card("Disks", snapshot.Disks.Count + " mounted file systems", VStack(AppTheme.SpacingM, bars.ToArray()));
    }

    /// <summary>Label plus a monospaced value, so changing digits do not jitter.</summary>
    private static Element Stat(string label, string value) =>
        VStack(
            2,
            Caption(label).Foreground(Theme.SecondaryText),
            BodyStrong(value).FontFamily(AppTheme.Monospace));
}
