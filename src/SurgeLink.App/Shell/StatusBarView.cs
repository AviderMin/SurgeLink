using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
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

        var items = new List<Element>
        {
            TextBlock(active is null ? "\uE8C8" : AppTheme.StatusGlyph(state))
                .FontFamily(SymbolFont)
                .FontSize(12)
                .Foreground(active is null ? Theme.SecondaryText : AppTheme.StatusBrush(state))
                .VAlign(VerticalAlignment.Center)
                .AutomationName(active is null ? "No session" : AppTheme.StatusText(state)),
            Caption(active is null ? "Ready" : AppTheme.StatusText(state))
                .Foreground(Theme.PrimaryText)
                .VAlign(VerticalAlignment.Center)
                .AutomationId("StatusConnectionState"),
        };

        if (active is not null)
        {
            items.Add(Divider());
            items.Add(Caption(Endpoint(active)).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
            items.Add(Caption(active.Session.Protocol.DisplayName).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
            items.Add(Caption(active.Session.HostInfo.Summary).Foreground(Theme.TertiaryText).VAlign(VerticalAlignment.Center));
        }

        if (!string.IsNullOrWhiteSpace(active?.Message))
        {
            items.Add(Divider());
            items.Add(Caption(active!.Message!).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
        }

        if (active is not null && active.State is ConnectionState.Disconnected or ConnectionState.Faulted)
        {
            items.Add(Button("Reconnect", () => props.Reconnect(active))
                .SubtleButton()
                .AutomationName("Reconnect session")
                .AutomationId("ReconnectButton"));
        }

        items.Add(Border(Empty()).Flex(grow: 1, basis: 0));
        items.Add(Caption($"{snapshot.Tabs.Count} session{(snapshot.Tabs.Count == 1 ? string.Empty : "s")}")
            .Foreground(Theme.SecondaryText)
            .VAlign(VerticalAlignment.Center)
            .AutomationId("StatusSessionCount"));
        items.Add(Caption(active is null ? "SurgeLink" : $"{active.Profile.Username}@{active.Profile.Host}")
            .Foreground(Theme.TertiaryText)
            .VAlign(VerticalAlignment.Center));

        return Border(HStack(AppTheme.SpacingM, items.ToArray()).Padding(AppTheme.SpacingM, 0))
            .Background(Theme.LayerFill)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0)
            .MinHeight(AppTheme.StatusBarHeight)
            .AutomationId("StatusBar");
    }

    private static readonly Microsoft.UI.Xaml.Media.FontFamily SymbolFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static Element Divider() =>
        TextBlock("\u00b7").Foreground(Theme.TertiaryText).VAlign(VerticalAlignment.Center);

    private static string Endpoint(WorkspaceTab tab) =>
        $"{tab.Profile.Username}@{tab.Profile.Host}" +
        (tab.Profile.Port > 0 ? ":" + tab.Profile.Port : string.Empty);
}
