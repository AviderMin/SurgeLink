using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Sessions;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for <see cref="StatusBarView"/>.</summary>
internal sealed record StatusBarProps(
    WorkspaceSnapshot Snapshot,
    AppSettings Settings,
    Action<WorkspaceTab> Reconnect);

/// <summary>
/// Bottom status strip: connection state, endpoint, host summary, the reconnect
/// action and a session counter. Everything is derived from the workspace
/// snapshot, so it stays in sync with the tabs above it.
/// </summary>
internal static class StatusBarView
{
    public static Element Render(StatusBarProps props)
    {
        var snapshot = props.Snapshot;
        var active = snapshot.ActiveTab;
        var state = active?.State ?? ConnectionState.Disconnected;

        var status = HStack(AppTheme.SpacingS,
            TextBlock(active is null ? "\uE8C8" : AppTheme.StatusGlyph(state))
                .FontFamily(SymbolFont).FontSize(12)
                .Foreground(active is null ? Theme.SecondaryText : AppTheme.StatusBrush(state))
                .VAlign(VerticalAlignment.Center)
                .AutomationName(active is null ? Str.T("shell.status.noSession", "No session") : AppTheme.StatusText(state)),
            Caption(active is null ? Str.T("shell.status.ready", "Ready") : AppTheme.StatusText(state))
                .VAlign(VerticalAlignment.Center)
                .AutomationId("StatusConnectionState"))
            .VAlign(VerticalAlignment.Center);

        var detail = active is null ? string.Empty :
            $"{Endpoint(active)} · {active.Session.Protocol.DisplayName} · {active.Session.HostInfo.Summary}";
        if (!string.IsNullOrWhiteSpace(active?.Message))
        {
            detail += " · " + active.Message;
        }

        var reconnect = Str.T("shell.status.reconnect", "Reconnect");
        var trailing = HStack(AppTheme.SpacingM,
            active is not null && active.State is ConnectionState.Disconnected or ConnectionState.Faulted
                ? Button(reconnect, () => props.Reconnect(active)).SubtleButton()
                    .AutomationName(reconnect).AutomationId("ReconnectButton")
                : Empty(),
            Caption(snapshot.Tabs.Count == 1
                    ? Str.F("shell.status.sessionsOne", "{0} session", snapshot.Tabs.Count)
                    : Str.F("shell.status.sessionsMany", "{0} sessions", snapshot.Tabs.Count))
                .Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                .AutomationId("StatusSessionCount"))
            .VAlign(VerticalAlignment.Center);

        return Border(Grid(
                columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Star()],
                status.Grid(column: 0),
                Caption(detail).Private(detail).Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(detail)
                    .VAlign(VerticalAlignment.Center).Margin(AppTheme.SpacingM, 0)
                    .Grid(column: 1),
                trailing.Grid(column: 2)).Padding(AppTheme.SpacingM, 0))
            .Background(Theme.LayerFill)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0)
            .MinHeight(AppTheme.StatusBarHeight)
            .AutomationId("StatusBar");
    }

    private static readonly Microsoft.UI.Xaml.Media.FontFamily SymbolFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static string Endpoint(WorkspaceTab tab) =>
        tab.Profile.UserHost +
        (tab.Profile.Port > 0 ? ":" + tab.Profile.Port : string.Empty);
}
