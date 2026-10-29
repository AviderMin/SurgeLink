using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.App.Views.Monitor;
using SurgeLink.Core.Monitoring;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for the compact monitor pinned to the bottom of the session sidebar.</summary>
internal sealed record SidebarMonitorProps(
    IRemoteSystemMonitor? Monitor,
    bool HasActiveSession,
    bool IsConnected,
    IAppSettingsStore Settings);

/// <summary>
/// A small automotive style instrument cluster for the active session: two
/// radial gauges for CPU and RAM, with network throughput on the left and disk
/// read/write throughput on the right. It samples the same session monitor as
/// the full dashboard but renders only the values that fit in the navigation
/// pane. When there is no active session or the protocol has no monitor, the
/// same footprint renders a short explanatory state.
/// </summary>
internal sealed class SidebarMonitor : Component<SidebarMonitorProps>
{
    private static readonly int[] Intervals = [1, 2, 5, 10, 30];

    public override Element Render()
    {
        var monitor = Props.Monitor;
        var settingsStore = Props.Settings;

        var settings = UseExternalStore(settingsStore.Subscribe, () => settingsStore.Current);

        var (snapshot, setSnapshot) = UseState<SystemSnapshot?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var historyRef = UseRef<MonitorHistory?>(null);
        historyRef.Current ??= new MonitorHistory();

        var intervalSeconds = Normalize(settings.MonitorIntervalSeconds);
        UseEffect(
            () =>
            {
                if (monitor is null)
                {
                    setSnapshot(null);
                    setError(null);
                    return () => { };
                }

                var history = new MonitorHistory();
                historyRef.Current = history;
                setSnapshot(null);
                setError(null);
                var subscription = RemoteMonitorSampler.For(monitor).Subscribe(
                    TimeSpan.FromSeconds(intervalSeconds), false, result =>
                    {
                        if (result.Snapshot is { } sample)
                        {
                            history.Add(sample);
                            setSnapshot(sample);
                        }
                        setError(result.Error);
                    });
                return subscription.Dispose;
            },
            monitor,
            intervalSeconds);

        if (monitor is null)
        {
            return BuildPlaceholder(Props.HasActiveSession, Props.IsConnected);
        }

        var history = historyRef.Current!;
        var cpu = snapshot is null ? 0d : snapshot.Cpu.TotalPercent / 100d;
        var memory = snapshot is null ? 0d : snapshot.Memory.UsedFraction;
        var cpuText = snapshot is null ? "—" : DataFormat.Percent(snapshot.Cpu.TotalPercent / 100d);
        var memoryText = snapshot is null ? "—" : DataFormat.Percent(snapshot.Memory.UsedFraction);
        var down = snapshot is null ? "—" : DataFormat.Rate(history.ReceiveRate);
        var up = snapshot is null ? "—" : DataFormat.Rate(history.SendRate);
        var read = snapshot is null ? "—" : DataFormat.Rate(snapshot.DiskReadBytesPerSecond);
        var write = snapshot is null ? "—" : DataFormat.Rate(snapshot.DiskWriteBytesPerSecond);

        var gauges = Grid(
            columns: [GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto],
            Component<Gauge, GaugeProps>(new GaugeProps(
                    cpu,
                    cpuText,
                    Str.T("shell.sidebar.monitor.cpu", "CPU"),
                    84,
                    CaptionBelowRing: true))
                .AutomationName(Str.T("shell.sidebar.monitor.cpuAutomation", "CPU total usage"))
                .HAlign(HorizontalAlignment.Center)
                .Grid(column: 0),
            Component<Gauge, GaugeProps>(new GaugeProps(
                    memory,
                    memoryText,
                    Str.T("shell.sidebar.monitor.ram", "RAM"),
                    84,
                    CaptionBelowRing: true))
                .AutomationName(Str.T("shell.sidebar.monitor.ramAutomation", "RAM usage"))
                .HAlign(HorizontalAlignment.Center)
                .Grid(column: 1)) with
        {
            ColumnSpacing = AppTheme.SpacingS,
        };

        var network = VStack(
            AppTheme.SpacingXs,
            Caption(Str.T("shell.sidebar.monitor.network", "Network")).Foreground(Theme.SecondaryText),
            MetricRow(Str.T("shell.sidebar.monitor.down", "↓"), down),
            MetricRow(Str.T("shell.sidebar.monitor.up", "↑"), up));

        var disk = VStack(
            AppTheme.SpacingXs,
            Caption(Str.T("shell.sidebar.monitor.disk", "Disk")).Foreground(Theme.SecondaryText),
            MetricRow(Str.T("shell.sidebar.monitor.read", "R"), read),
            MetricRow(Str.T("shell.sidebar.monitor.write", "W"), write));

        var throughput = Grid(
            columns: [GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto],
            network.Grid(column: 0),
            disk.Grid(column: 1)) with
        {
            ColumnSpacing = AppTheme.SpacingM,
        };

        return Border(
                VStack(
                    AppTheme.SpacingS,
                    Caption(Str.T("shell.sidebar.monitor.title", "Remote monitor"))
                        .Foreground(Theme.SecondaryText),
                    gauges,
                    throughput,
                    error is null ? null : Caption(error)
                        .Foreground(Theme.SystemCritical)
                        .TextTrimming(TextTrimming.CharacterEllipsis)))
            .Padding(AppTheme.SpacingM)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.DividerStroke, 1)
            .CornerRadius(AppTheme.CardRadius)
            .AutomationId("SidebarMonitor")
            .AutomationName(Str.T("shell.sidebar.monitor.automation", "Remote monitor"));
    }

    private static Element MetricRow(string label, string value) =>
        HStack(
            AppTheme.SpacingXs,
            Caption(label).Foreground(Theme.Accent).Width(12),
            Caption(value)
                .Foreground(Theme.PrimaryText)
                .FontFamily(AppTheme.Monospace)
                .TextTrimming(TextTrimming.CharacterEllipsis));

    private static Element BuildPlaceholder(bool hasActiveSession, bool isConnected)
    {
        // Distinguish "no session open", "session not connected" and "connected
        // protocol without counters": the old two-state message claimed a
        // disconnected session did not expose performance counters.
        var message = !hasActiveSession
            ? Str.T("shell.sidebar.monitor.noSession", "Open a session to see live performance.")
            : !isConnected
                ? Str.T("shell.sidebar.monitor.disconnected", "Reconnect the session to see live performance.")
                : Str.T("shell.sidebar.monitor.unavailable", "This session does not expose performance counters.");
        return Border(
                VStack(
                    AppTheme.SpacingXs,
                    Caption(Str.T("shell.sidebar.monitor.title", "Remote monitor"))
                        .Foreground(Theme.SecondaryText),
                    Caption(message)
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(TextWrapping.Wrap)))
            .Padding(AppTheme.SpacingM)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.DividerStroke, 1)
            .CornerRadius(AppTheme.CardRadius)
            .AutomationId("SidebarMonitor")
            .AutomationName(Str.T("shell.sidebar.monitor.automation", "Remote monitor"));
    }

    private static int Normalize(int seconds) => Intervals.Contains(seconds) ? seconds : 2;
}
