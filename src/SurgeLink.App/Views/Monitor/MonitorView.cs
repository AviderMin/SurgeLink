using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Components;
using SurgeLink.App.Styling;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Monitor;

/// <summary>
/// The remote performance dashboard. One sampling loop feeds cards, charts and
/// the process table; the loop honours <see cref="AppSettings.MonitorIntervalSeconds"/>
/// and stops while paused. Chart history lives outside the render tree so a
/// re-render never restarts the rolling window.
/// </summary>
internal sealed class MonitorView : Component<MonitorViewProps>
{
    private static readonly int[] Intervals = [1, 2, 5, 10];

    public override Element Render()
    {
        var monitor = Props.Monitor;
        var settingsStore = Props.Settings;

        // Hooks: constant order and count on every render.
        var (settings, setSettings) = UseState(settingsStore.Current);
        UseEffect(
            () =>
            {
                setSettings(settingsStore.Current);
                return settingsStore.Subscribe(() => setSettings(settingsStore.Current));
            },
            settingsStore);

        var (snapshot, setSnapshot) = UseState<SystemSnapshot?>(null, threadSafe: true);
        var (processes, setProcesses) = UseState<IReadOnlyList<RemoteProcessInfo>>(Array.Empty<RemoteProcessInfo>(), threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (updatedAt, setUpdatedAt) = UseState<DateTimeOffset?>(null, threadSafe: true);
        var (paused, setPaused) = UseState(false);
        var (reload, setReload) = UseState(0);
        var wide = UseBreakpoint(1100);
        var historyRef = UseRef<MonitorHistory?>(null);
        historyRef.Current ??= new MonitorHistory();
        var history = historyRef.Current;

        var intervalSeconds = Normalize(settings.MonitorIntervalSeconds);
        var runKey = paused ? "paused" : "run:" + reload;

        UseEffect(
            () =>
            {
                var target = monitor;
                if (target is null || paused)
                {
                    return () => { };
                }

                var cancellation = new CancellationTokenSource();
                var interval = TimeSpan.FromSeconds(intervalSeconds);
                var clock = history;

                _ = Task.Run(async () =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        try
                        {
                            var sample = await target.SampleAsync(cancellation.Token).ConfigureAwait(false);

                            IReadOnlyList<RemoteProcessInfo> top;
                            try
                            {
                                top = await target.GetTopProcessesAsync(15, cancellation.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                // The process list is optional; the counters still render.
                                top = Array.Empty<RemoteProcessInfo>();
                            }

                            clock.Add(sample);
                            setSnapshot(sample);
                            setProcesses(top);
                            setError(null);
                            setUpdatedAt(DateTimeOffset.Now);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            setError(ex.Message);
                        }

                        try
                        {
                            await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                });

                return () => cancellation.Cancel();
            },
            monitor,
            intervalSeconds,
            runKey);

        if (monitor is null)
        {
            return Unavailable();
        }

        var header = BuildHeader(
            paused,
            intervalSeconds,
            updatedAt,
            () => setPaused(!paused),
            () => setReload(reload + 1),
            index =>
            {
                if (index >= 0 && index < Intervals.Length)
                {
                    settingsStore.Update(current => current with { MonitorIntervalSeconds = Intervals[index] });
                }
            });

        var banner = error is null
            ? null
            : (InfoBar("Monitor unavailable", error) with
            {
                Severity = InfoBarSeverity.Error,
                ActionButtonContent = "Retry",
                OnActionButtonClick = () =>
                {
                    setError(null);
                    setReload(reload + 1);
                },
                IsClosable = false,
            });

        Element body;
        if (snapshot is null)
        {
            body = error is null
                ? Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Star()],
                    ProgressRing().HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                : Caption("Waiting for the first successful sample.").Foreground(Theme.SecondaryText);
        }
        else
        {
            body = BuildCards(snapshot, processes, history, wide);
        }

        return ScrollView(
                VStack(
                    AppTheme.SpacingL,
                    header,
                    banner,
                    body))
            .Padding(AppTheme.SpacingL);
    }

    private static Element BuildHeader(
        bool paused,
        int intervalSeconds,
        DateTimeOffset? updatedAt,
        Action togglePause,
        Action sampleNow,
        Action<int> selectInterval)
    {
        var currentIndex = Array.IndexOf(Intervals, intervalSeconds);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var intervalSelector = ComboBox(
                Intervals.Select(value => value + " s").ToArray(),
                Optional<int>.Of(currentIndex),
                selectInterval)
            .AutomationName("Sampling interval")
            .MinWidth(96)
            .IsEnabled(!paused);

        return Grid(
            columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
            rows: [GridSize.Auto],
            SubHeading("Remote performance")
                .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1)
                .Grid(row: 0, column: 0),
            Caption(updatedAt is null
                    ? "Waiting for the first sample"
                    : "Last update " + DataFormat.Clock(updatedAt.Value))
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
                .Margin(AppTheme.SpacingM, 0, 0, 0)
                .Grid(row: 0, column: 1),
            intervalSelector.Grid(row: 0, column: 2).Margin(0, 0, AppTheme.SpacingS, 0),
            HStack(
                    AppTheme.SpacingS,
                    Button(paused ? "Resume" : "Pause", togglePause)
                        .ToolTip(paused ? "Resume live sampling" : "Pause live sampling")
                        .AutomationName(paused ? "Resume sampling" : "Pause sampling"),
                    Button(Icon(FontIcon("\uE72C", fontSize: 14)), sampleNow)
                        .SubtleButton()
                        .ToolTip("Sample now")
                        .AutomationName("Sample now"))
                .Grid(row: 0, column: 3));
    }

    private static Element BuildCards(
        SystemSnapshot snapshot,
        IReadOnlyList<RemoteProcessInfo> processes,
        MonitorHistory history,
        bool twoColumns)
    {
        var cpu = MonitorCards.Cpu(snapshot, history);
        var memory = MonitorCards.Memory(snapshot);
        var network = MonitorCards.Network(snapshot, history);
        var host = MonitorCards.Host(snapshot);
        var disks = MonitorCards.Disks(snapshot);
        var processCard = Component<MetricCard, MetricCardProps>(new MetricCardProps(
            "Top processes",
            "Busiest " + Math.Min(15, processes.Count) + " processes",
            Component<MonitorProcessTable, MonitorProcessTableProps>(
                new MonitorProcessTableProps(processes))));

        if (!twoColumns)
        {
            return Grid(
                columns: [GridSize.Star()],
                rows:
                [
                    GridSize.Auto,
                    GridSize.Auto,
                    GridSize.Auto,
                    GridSize.Auto,
                    GridSize.Auto,
                    GridSize.Auto,
                ],
                cpu.Grid(row: 0),
                memory.Grid(row: 1),
                network.Grid(row: 2),
                host.Grid(row: 3),
                disks.Grid(row: 4),
                processCard.Grid(row: 5)) with { RowSpacing = AppTheme.SpacingL };
        }

        return Grid(
            columns: [GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
            cpu.Grid(row: 0, column: 0),
            memory.Grid(row: 0, column: 1),
            network.Grid(row: 1, column: 0),
            host.Grid(row: 1, column: 1),
            disks.Grid(row: 2, column: 0, columnSpan: 2),
            processCard.Grid(row: 3, column: 0, columnSpan: 2)) with
        {
            ColumnSpacing = AppTheme.SpacingL,
            RowSpacing = AppTheme.SpacingL,
        };
    }

    private static int Normalize(int seconds) => Intervals.Contains(seconds) ? seconds : 2;

    private static Element Unavailable() =>
        Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            VStack(
                    AppTheme.SpacingS,
                    Icon(FontIcon("\uE9D9", fontSize: 32)),
                    BodyStrong("Performance monitoring is not available"),
                    Caption("This protocol does not expose remote system counters, so there is nothing to chart.")
                        .Foreground(Theme.SecondaryText)
                        .MaxWidth(420)
                        .TextWrapping(TextWrapping.Wrap))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center));
}
