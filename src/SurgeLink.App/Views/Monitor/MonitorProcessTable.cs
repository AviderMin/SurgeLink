using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Styling;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Monitor;

/// <summary>Props for <see cref="MonitorProcessTable"/>.</summary>
/// <param name="Processes">Processes reported by the remote host.</param>
/// <param name="MaxRows">How many rows to show, busiest first.</param>
internal sealed record MonitorProcessTableProps(IReadOnlyList<RemoteProcessInfo> Processes, int MaxRows = 15);

/// <summary>
/// Top processes table: PID, user, CPU%, memory%, resident set size and the
/// command line. Columns sort by clicking their header; the table keeps its own
/// sort state so re-sampling only replaces the data.
/// </summary>
internal sealed class MonitorProcessTable : Component<MonitorProcessTableProps>
{
    private static readonly GridSize[] Columns =
    [
        GridSize.Px(72),
        GridSize.Px(104),
        GridSize.Px(72),
        GridSize.Px(72),
        GridSize.Px(88),
        GridSize.Star(),
    ];

    private static readonly string[] Headers = ["PID", "User", "CPU", "MEM", "RSS", "Command"];

    public override Element Render()
    {
        // Column index of the active sort and its direction.
        var (sortIndex, setSortIndex) = UseState(2);
        var (descending, setDescending) = UseState(true);
        var sortKey = sortIndex + (descending ? "d" : "a");

        var rows = UseMemo(
            () => Sort(Props.Processes, sortIndex, descending).Take(Math.Max(1, Props.MaxRows)).ToList(),
            Props.Processes,
            sortKey);

        if (Props.Processes.Count == 0)
        {
            return Caption("Process list is not available on this host.").Foreground(Theme.SecondaryText);
        }

        var headerCells = new List<Element>(Headers.Length);
        for (var index = 0; index < Headers.Length; index++)
        {
            var column = index;
            var active = sortIndex == column;
            headerCells.Add(
                Button(
                        HStack(
                            AppTheme.SpacingXs,
                            Caption(Headers[column]).SemiBold(),
                            active ? Icon(FontIcon(descending ? "\uE70D" : "\uE70E", fontSize: 10)) : null),
                        () =>
                        {
                            if (sortIndex == column)
                            {
                                setDescending(!descending);
                            }
                            else
                            {
                                setSortIndex(column);
                                setDescending(true);
                            }
                        })
                    .SubtleButton()
                    .AutomationName("Sort processes by " + Headers[column])
                    .MinHeight(32)
                    .Grid(row: 0, column: column));
        }

        var body = new List<Element>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var process = rows[index];
            body.Add(Row(process).Grid(row: index));
        }

        return VStack(
            AppTheme.SpacingXs,
            Grid(
                columns: Columns,
                rows: [GridSize.Auto],
                headerCells.ToArray()).Padding(AppTheme.SpacingS, 0),
            Border(Empty()).Height(1).Background(Theme.DividerStroke),
            ScrollView(
                    Grid(
                        columns: [GridSize.Star()],
                        rows: rows.Select((_, index) => GridSize.Auto).ToArray(),
                        body.ToArray()))
                .MaxHeight(320));
    }

    private static Element Row(RemoteProcessInfo process) =>
        Grid(
                columns: Columns,
                rows: [GridSize.Auto],
                TextBlock(process.ProcessId.ToString()).FontFamily(AppTheme.Monospace).FontSize(12).Grid(row: 0, column: 0),
                TextBlock(process.User ?? "n/a")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 1),
                TextBlock(DataFormat.PercentValue(process.CpuPercent))
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Grid(row: 0, column: 2),
                TextBlock(DataFormat.PercentValue(process.MemoryPercent))
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Grid(row: 0, column: 3),
                TextBlock(DataFormat.Bytes(process.ResidentBytes))
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Grid(row: 0, column: 4),
                TextBlock(string.IsNullOrEmpty(process.CommandLine) ? process.Name : process.CommandLine)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 5))
            .Padding(0, 4, 0, 4)
            .AutomationName(process.Name + " " + process.ProcessId);

    private static IEnumerable<RemoteProcessInfo> Sort(IReadOnlyList<RemoteProcessInfo> processes, int column, bool descending)
    {
        Func<RemoteProcessInfo, object> key = column switch
        {
            0 => process => process.ProcessId,
            1 => process => process.User ?? string.Empty,
            3 => process => process.MemoryPercent,
            4 => process => process.ResidentBytes,
            5 => process => process.CommandLine ?? process.Name,
            _ => process => process.CpuPercent,
        };

        return descending
            ? processes.OrderByDescending(key)
            : processes.OrderBy(key);
    }
}
