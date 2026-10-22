using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.App.Sessions;
using SurgeLink.App.Views.Monitor;
using SurgeLink.App.Views.Sftp;
using SurgeLink.App.Views.Terminal;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for <see cref="SessionWorkspaceView"/>; a new record per state change drives re-render.</summary>
internal sealed record SessionViewProps(WorkspaceTab Tab, ConnectionState State, SessionViewKind View, string? Message);

/// <summary>
/// One session tab: a capability switcher (Terminal / Files / Monitor) plus the
/// active view. Views are keyed by kind so their state survives tab switches.
/// </summary>
internal sealed class SessionWorkspaceView : Component<SessionViewProps>
{
    /// <summary>Snapshots the tab into immutable props.</summary>
    public static SessionViewProps PropsFor(WorkspaceTab tab) =>
        new(tab, tab.State, tab.View, tab.Message);

    public override Element Render()
    {
        var services = UseContext(AppServices.ReactorContext)
            ?? throw new InvalidOperationException("The session workspace must be inside the shell service context.");
        var props = Props;
        var tab = props.Tab;
        var capabilities = tab.Session.Protocol.Capabilities;
        var views = new List<SessionViewKind>(3);
        var items = new List<SelectorBarItemData>(3);

        if ((capabilities & SessionCapabilities.Terminal) != 0)
        {
            views.Add(SessionViewKind.Terminal);
            items.Add(new SelectorBarItemData(Str.T("shell.workspace.tab.terminal", "Terminal")));
        }

        if ((capabilities & SessionCapabilities.FileSystem) != 0)
        {
            views.Add(SessionViewKind.Files);
            items.Add(new SelectorBarItemData(Str.T("shell.workspace.tab.files", "Files")));
        }

        if ((capabilities & SessionCapabilities.Monitor) != 0)
        {
            views.Add(SessionViewKind.Monitor);
            items.Add(new SelectorBarItemData(Str.T("shell.workspace.tab.monitor", "Monitor")));
        }

        var selected = Math.Max(0, views.IndexOf(props.View));

        var selector = SelectorBar(
            items.ToArray(),
            Optional<int>.Of(selected),
            index =>
            {
                if (index >= 0 && index < views.Count)
                {
                    services.Workspace.SetView(tab.Id, views[index]);
                }
            });

        var header = Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                VStack(AppTheme.SpacingXs,
                    BodyStrong(tab.Title).TextTrimming(TextTrimming.CharacterEllipsis),
                    Caption(tab.Profile.Username + "@" + tab.Profile.Host)
                        .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis))
                    .VAlign(VerticalAlignment.Center)
                    .ToolTip(string.IsNullOrEmpty(props.Message) ? tab.Title : props.Message)
                    .Margin(0, 0, AppTheme.SpacingM, 0).Grid(column: 0),
                selector.VAlign(VerticalAlignment.Center).Grid(column: 1))
            .Padding(AppTheme.SpacingM, AppTheme.SpacingS)
            .MinHeight(56);

        Element content = props.View switch
        {
            SessionViewKind.Files => Component<SftpView, SftpViewProps>(
                new SftpViewProps(
                    tab.Session.FileSystem,
                    tab.Profile,
                    services.Settings,
                    tab.Transfers,
                    tab.FileBrowserPath,
                    path => services.Workspace.SetFileBrowserPath(tab.Id, path), tab.Session)),
            SessionViewKind.Monitor => Component<MonitorView, MonitorViewProps>(
                new MonitorViewProps(tab.Session.Monitor, services.Settings)),
            _ => Component<TerminalView, TerminalViewProps>(new TerminalViewProps(tab.Terminal)),
        };

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            header.Grid(row: 0),
            content.Grid(row: 1));
    }

    private static Element Box() => Border(Empty()).Flex(grow: 1, basis: 0);
}
