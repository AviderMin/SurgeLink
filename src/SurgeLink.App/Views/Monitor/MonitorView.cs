using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Monitor;

/// <summary>
/// The remote performance dashboard. One sampling loop feeds cards, charts and
/// the process table; the loop honours <see cref="AppSettings.MonitorIntervalSeconds"/>
/// and stops while paused. Chart history lives outside the render tree so a
/// re-render never restarts the rolling window, and every visible label is
/// resolved through <see cref="Str"/> so the language switch applies.
/// </summary>
internal sealed class MonitorView : Component<MonitorViewProps>
{
    private static readonly int[] Intervals = [1, 2, 5, 10, 30];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRemoteSystemMonitor, MonitorViewState> States = new();

    public override Element Render()
    {
        var monitor = Props.Monitor;
        var settingsStore = Props.Settings;

        // Hooks: constant order and count on every render.
        var settings = UseExternalStore(settingsStore.Subscribe, () => settingsStore.Current);
        var retained = UseMemo(() => monitor is null ? new MonitorViewState() : States.GetOrCreateValue(monitor), monitor!);

        var (snapshot, setSnapshot) = UseState<SystemSnapshot?>(retained.Snapshot, threadSafe: true);
        var (processes, setProcesses) = UseState(retained.Processes, threadSafe: true);
        var (error, setError) = UseState<string?>(retained.Error, threadSafe: true);
        var (updatedAt, setUpdatedAt) = UseState<DateTimeOffset?>(retained.UpdatedAt, threadSafe: true);
        var (paused, setPaused) = UseState(retained.Paused);
        var (reload, setReload) = UseState(0);
        var sampleRequested = UseRef(false);
        var (wide, setWide) = UseState(false);
        var history = retained.History;

        var intervalSeconds = Normalize(settings.MonitorIntervalSeconds);
        var runKey = (paused ? "paused:" : "run:") + reload;

        UseEffect(
            () =>
            {
                var target = monitor;
                var requested = sampleRequested.Current;
                sampleRequested.Current = false;
                if (target is null || !Props.IsActive || (paused && !requested))
                {
                    return () => { };
                }

                var subscription = RemoteMonitorSampler.For(target).Subscribe(
                    TimeSpan.FromSeconds(intervalSeconds), true, result =>
                    {
                        if (result.Snapshot is { } sample)
                        {
                            history.Add(sample);
                            retained.Snapshot = sample;
                            retained.Processes = result.Processes;
                            retained.UpdatedAt = DateTimeOffset.Now;
                            setSnapshot(sample);
                            setProcesses(result.Processes);
                            setUpdatedAt(retained.UpdatedAt);
                        }
                        retained.Error = result.Error;
                        setError(result.Error);
                    }, once: paused);
                return subscription.Dispose;
            },
            monitor!,
            intervalSeconds,
            Props.IsActive,
            runKey);

        if (monitor is null)
        {
            return Unavailable();
        }

        var header = BuildHeader(
            paused,
            intervalSeconds,
            updatedAt,
            () => { retained.Paused = !paused; setPaused(retained.Paused); },
            () =>
            {
                sampleRequested.Current = true;
                setReload(reload + 1);
            },
            index =>
            {
                if (index >= 0 && index < Intervals.Length)
                {
                    settingsStore.Update(current => current with { MonitorIntervalSeconds = Intervals[index] });
                }
            },
            wide);

        var banner = error is null
            ? null
            : (InfoBar(Str.T("monitor.unavailable.title", "Monitor unavailable"), error) with
            {
                Severity = InfoBarSeverity.Error,
                ActionButtonContent = Str.T("monitor.retry", "Retry"),
                OnActionButtonClick = () =>
                {
                    setError(null);
                    sampleRequested.Current = true;
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
                    .MinHeight(320)
                : Caption(Str.T("monitor.waitingSuccessfulSample", "Waiting for the first successful sample."))
                    .Foreground(Theme.SecondaryText);
        }
        else
        {
            body = BuildCards(snapshot, processes, history);
        }

        // The viewport supplies width; host text and CPU count only affect height.
        var page = Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                header.Grid(row: 0),
                banner?.Grid(row: 1),
                body.Grid(row: 2)) with
        {
            RowSpacing = AppTheme.SpacingXl,
            Padding = new Thickness(AppTheme.SpacingXl),
        };

        return Border(
                ScrollView(page.HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Top)) with
                {
                    ContentOrientation = ScrollingContentOrientation.Vertical,
                    HorizontalScrollMode = ScrollingScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
                    VerticalScrollMode = ScrollingScrollMode.Enabled,
                    // Reserve the same viewport width on every host and sample.
                    VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Visible,
                })
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnSizeChanged((_, args) =>
            {
                // Measure the page viewport, not the desktop width: the sidebar
                // and the window chrome are not part of the dashboard.
                var nextWide = args.NewSize.Width - (2 * AppTheme.SpacingXl) >= 760;
                if (nextWide != wide)
                {
                    setWide(nextWide);
                }
            });
    }

    private static Element BuildHeader(
        bool paused,
        int intervalSeconds,
        DateTimeOffset? updatedAt,
        Action togglePause,
        Action sampleNow,
        Action<int> selectInterval,
        bool wide)
    {
        var currentIndex = Array.IndexOf(Intervals, intervalSeconds);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var intervalSelector = ComboBox(
                Intervals.Select(value => Str.F("monitor.interval.seconds", "{0} s", value)).ToArray(),
                Optional<int>.Of(currentIndex),
                selectInterval)
            .AutomationName(Str.T("monitor.interval.automation", "Sampling interval"))
            .MinWidth(96)
            .IsEnabled(!paused);

        var controls = HStack(
                AppTheme.SpacingS,
                intervalSelector,
                Button(
                        paused
                            ? Str.T("monitor.resume", "Resume")
                            : Str.T("monitor.pause", "Pause"),
                        togglePause)
                    .ToolTip(paused
                        ? Str.T("monitor.resumeToolTip", "Resume live sampling")
                        : Str.T("monitor.pauseToolTip", "Pause live sampling"))
                    .AutomationName(paused
                        ? Str.T("monitor.resumeAutomation", "Resume sampling")
                        : Str.T("monitor.pauseAutomation", "Pause sampling")),
                Button(Icon(FontIcon("\uE72C", fontSize: 14)), sampleNow)
                    .SubtleButton()
                    .ToolTip(Str.T("monitor.sampleNowToolTip", "Sample now"))
                    .AutomationName(Str.T("monitor.sampleNow", "Sample now")));

        var title = VStack(
                AppTheme.SpacingXs,
                Heading(Str.T("monitor.title", "Remote performance"))
                    .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1),
                Caption(updatedAt is null
                        ? Str.T("monitor.waitingFirstSample", "Waiting for the first sample")
                        : Str.F("monitor.lastUpdate", "Last update {0}", DataFormat.Clock(updatedAt.Value)))
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis));

        if (!wide)
        {
            return VStack(
                AppTheme.SpacingM,
                title,
                controls.HAlign(HorizontalAlignment.Left));
        }

        return Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                title.Grid(row: 0, column: 0),
                controls.Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center))
            with
            {
                ColumnSpacing = AppTheme.SpacingL,
            };
    }

    private static Element BuildCards(
        SystemSnapshot snapshot,
        IReadOnlyList<RemoteProcessInfo> processes,
        MonitorHistory history)
    {
        var cpu = MonitorCards.Cpu(snapshot, history);
        var memory = MonitorCards.Memory(snapshot);
        var network = MonitorCards.Network(snapshot, history);
        var host = MonitorCards.Host(snapshot);
        var disks = MonitorCards.Disks(snapshot);
        var processCard = Component<MetricCard, MetricCardProps>(new MetricCardProps(
            Str.T("monitor.process.title", "Top processes"),
            Str.F("monitor.process.caption", "Busiest {0} processes", Math.Min(15, processes.Count)),
            Component<MonitorProcessTable, MonitorProcessTableProps>(
                new MonitorProcessTableProps(processes))));

        (string Key, Element Content)[] cards =
        [
            ("cpu", cpu), ("memory", memory), ("network", network),
            ("host", host), ("disks", disks), ("processes", processCard),
        ];
        return Grid(
            columns: [GridSize.Star()],
            rows: Enumerable.Repeat(GridSize.Auto, cards.Length).ToArray(),
            cards.Select((card, index) => Border(card.Content)
                .HAlign(HorizontalAlignment.Stretch)
                .Grid(row: index)
                .WithKey(card.Key)).ToArray()) with
        {
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
                    BodyStrong(Str.T("monitor.notAvailable.title", "Performance monitoring is not available")),
                    Caption(Str.T(
                            "monitor.notAvailable.hint",
                            "This protocol does not expose remote system counters, so there is nothing to chart."))
                        .Foreground(Theme.SecondaryText)
                        .MaxWidth(420)
                        .TextWrapping(TextWrapping.Wrap))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center));
}
