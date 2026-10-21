using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Monitor;

/// <summary>
/// The dashboard cards. Each builder returns a <see cref="MetricCard"/> and
/// degrades to a localized "n/a" caption when the protocol could not report that
/// section, which is what servers without <c>/proc</c> look like.
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
            ? Str.F("monitor.card.cpu.caption", "{0} average · {1} logical cores",
                DataFormat.PercentValue(cpu.TotalPercent), cores.Count)
            : Str.F("monitor.card.cpu.captionFallback", "{0} average · per core data not available",
                DataFormat.PercentValue(cpu.TotalPercent));

        Element detail;
        if (cores.Count == 0)
        {
            detail = Caption(Str.T("monitor.card.cpu.perCoreOnly", "This host reports only the total CPU utilisation."))
                .Foreground(Theme.SecondaryText);
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
                        .Grid(row: index / 2, column: index % 2)
                        // Logical core index is the stable identity of a core.
                        .WithKey("cpu-core:" + index));
            }

            detail = Grid(
                columns: [GridSize.Star(), GridSize.Star()],
                rows: Enumerable.Repeat(GridSize.Auto, (cores.Count + 1) / 2).ToArray(),
                perCore.ToArray()) with
            {
                ColumnSpacing = AppTheme.SpacingM,
                RowSpacing = AppTheme.SpacingS,
            };
        }

        var body = VStack(
            AppTheme.SpacingM,
            Grid(
                columns: [GridSize.Auto, GridSize.Star()],
                rows: [GridSize.Auto],
                Component<Gauge, GaugeProps>(new GaugeProps(
                        cpu.TotalPercent / 100d,
                        DataFormat.PercentValue(cpu.TotalPercent),
                        Str.T("monitor.card.cpu.total", "total"),
                        112))
                    .Grid(row: 0, column: 0),
                detail.Grid(row: 0, column: 1).Margin(AppTheme.SpacingL, 0, 0, 0)),
            Component<LineChart, LineChartProps>(new LineChartProps(
                    [new ChartSeries(Str.T("monitor.chart.seriesCpu", "CPU"), history.CpuHistory)],
                    Height: 64,
                    MaxValue: 100))
                .AutomationName(Str.T("monitor.card.cpu.history", "CPU history")));

        return Card(Str.T("monitor.card.cpu.title", "CPU"), caption, body);
    }

    /// <summary>Memory and swap usage.</summary>
    public static Element Memory(SystemSnapshot snapshot)
    {
        var memory = snapshot.Memory;
        if (memory.TotalBytes <= 0)
        {
            return Card(
                Str.T("monitor.card.memory.title", "Memory"),
                null,
                Caption(Str.T("monitor.card.memory.unavailable", "Memory counters are not available on this host."))
                    .Foreground(Theme.SecondaryText));
        }

        var swapFraction = memory.SwapTotalBytes > 0
            ? Math.Clamp((double)memory.SwapUsedBytes / memory.SwapTotalBytes, 0d, 1d)
            : 0d;

        var bars = VStack(
            AppTheme.SpacingM,
            Component<UsageBar, UsageBarProps>(new UsageBarProps(
                memory.UsedFraction,
                Str.T("monitor.card.memory.physical", "Physical"),
                Str.F("monitor.card.memory.usage", "{0} of {1}",
                    DataFormat.Percent(memory.UsedFraction),
                    DataFormat.Bytes(memory.TotalBytes)))),
            memory.SwapTotalBytes > 0
                ? Component<UsageBar, UsageBarProps>(new UsageBarProps(
                    swapFraction,
                    Str.T("monitor.card.memory.swap", "Swap"),
                    Str.F("monitor.card.memory.usage", "{0} of {1}",
                        DataFormat.Percent(swapFraction),
                        DataFormat.Bytes(memory.SwapTotalBytes))))
                : Component<UsageBar, UsageBarProps>(new UsageBarProps(
                    0,
                    Str.T("monitor.card.memory.swap", "Swap"),
                    Str.T("monitor.card.memory.swapNotConfigured", "not configured"))),
            Caption(Str.F("monitor.card.memory.cached", "Cached {0} · available {1}",
                    DataFormat.Bytes(memory.CachedBytes),
                    DataFormat.Bytes(memory.AvailableBytes)))
                .Foreground(Theme.SecondaryText));

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
            Str.T("monitor.card.memory.title", "Memory"),
            Str.F("monitor.card.memory.caption", "{0} of {1} in use",
                DataFormat.Bytes(memory.UsedBytes),
                DataFormat.Bytes(memory.TotalBytes)),
            body);
    }

    /// <summary>Network throughput with a two series rolling chart.</summary>
    public static Element Network(SystemSnapshot snapshot, MonitorHistory history)
    {
        if (snapshot.Networks.Count == 0)
        {
            return Card(
                Str.T("monitor.card.network.title", "Network"),
                null,
                Caption(Str.T("monitor.card.network.unavailable", "Network counters are not available on this host."))
                    .Foreground(Theme.SecondaryText));
        }

        var interfaces = string.Join(" · ", snapshot.Networks.Select(network => network.InterfaceName));
        var body = VStack(
            AppTheme.SpacingM,
            Grid(
                columns: [GridSize.Star(), GridSize.Star()],
                rows: [GridSize.Auto],
                Stat(Str.T("monitor.card.network.download", "Download"), DataFormat.Rate(history.ReceiveRate))
                    .Grid(row: 0, column: 0),
                Stat(Str.T("monitor.card.network.upload", "Upload"), DataFormat.Rate(history.SendRate))
                    .Grid(row: 0, column: 1)).Set(grid => grid.ColumnSpacing = AppTheme.SpacingL),
            Component<LineChart, LineChartProps>(new LineChartProps(
                    [
                        new ChartSeries(Str.T("monitor.chart.seriesDownload", "Download"), history.ReceiveHistory),
                        new ChartSeries(Str.T("monitor.chart.seriesUpload", "Upload"), history.SendHistory),
                    ],
                    Height: 88))
                .AutomationName(Str.T("monitor.card.network.history", "Network throughput history")),
            Caption(Str.F("monitor.card.network.interfaces", "Interfaces: {0}", interfaces))
                .Foreground(Theme.SecondaryText));

        return Card(
            Str.T("monitor.card.network.title", "Network"),
            Str.F("monitor.card.network.caption", "Throughput over the last {0} samples", MonitorHistory.Capacity),
            body);
    }

    /// <summary>Load averages, uptime and process/thread counts.</summary>
    public static Element Host(SystemSnapshot snapshot)
    {
        var cpu = snapshot.Cpu;
        var hasLoad = cpu.Load1 > 0 || cpu.Load5 > 0 || cpu.Load15 > 0 || cpu.ProcessCount > 0;
        var na = Str.T("monitor.na", "n/a");

        var body = Grid(
            columns: [GridSize.Star(), GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto],
            Stat(Str.T("monitor.card.system.load1", "Load 1m"), hasLoad ? cpu.Load1.ToString("0.00") : na)
                .Grid(row: 0, column: 0),
            Stat(Str.T("monitor.card.system.load5", "Load 5m"), hasLoad ? cpu.Load5.ToString("0.00") : na)
                .Grid(row: 0, column: 1),
            Stat(Str.T("monitor.card.system.load15", "Load 15m"), hasLoad ? cpu.Load15.ToString("0.00") : na)
                .Grid(row: 0, column: 2),
            Stat(
                    Str.T("monitor.card.system.uptime", "Uptime"),
                    snapshot.Uptime is { } uptime ? DataFormat.Duration(uptime) : na)
                .Grid(row: 1, column: 0),
            Stat(
                    Str.T("monitor.card.system.processes", "Processes"),
                    cpu.ProcessCount > 0 ? DataFormat.Count(cpu.ProcessCount) : na)
                .Grid(row: 1, column: 1),
            Stat(
                    Str.T("monitor.card.system.threads", "Threads"),
                    cpu.ThreadCount > 0 ? DataFormat.Count(cpu.ThreadCount) : na)
                .Grid(row: 1, column: 2)) with
        {
            ColumnSpacing = AppTheme.SpacingM,
            RowSpacing = AppTheme.SpacingL,
        };

        return Card(
            Str.T("monitor.card.system.title", "System"),
            snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            body);
    }

    /// <summary>Disk usage bars, one per mount point.</summary>
    public static Element Disks(SystemSnapshot snapshot)
    {
        if (snapshot.Disks.Count == 0)
        {
            return Card(
                Str.T("monitor.card.disks.title", "Disks"),
                null,
                Caption(Str.T("monitor.card.disks.unavailable", "No mounted file systems were reported."))
                    .Foreground(Theme.SecondaryText));
        }

        var bars = new List<Element>(snapshot.Disks.Count);
        foreach (var disk in snapshot.Disks)
        {
            // Device + mount point is unique per row; a mount point alone can be
            // reused by a different device after a remount.
            var key = (disk.FileSystem ?? string.Empty) + "|" + disk.MountPoint;
            bars.Add(Component<UsageBar, UsageBarProps>(new UsageBarProps(
                disk.UsedFraction,
                disk.MountPoint,
                Str.F("monitor.card.disks.usage", "{0} of {1} used",
                    DataFormat.Percent(disk.UsedFraction),
                    DataFormat.Bytes(disk.TotalBytes)))).WithKey(key));
        }

        return Card(
            Str.T("monitor.card.disks.title", "Disks"),
            Str.F("monitor.card.disks.caption", "{0} mounted file systems", snapshot.Disks.Count),
            VStack(AppTheme.SpacingM, bars.ToArray()));
    }

    /// <summary>Label plus a monospaced value, so changing digits do not jitter.</summary>
    private static Element Stat(string label, string value) =>
        VStack(
            AppTheme.SpacingXs,
            Caption(label).Foreground(Theme.SecondaryText),
            BodyStrong(value).FontFamily(AppTheme.Monospace));
}
