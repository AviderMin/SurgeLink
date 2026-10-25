using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.App.Localization;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Props for <see cref="TerminalView"/>.</summary>
internal sealed record TerminalViewProps(TerminalBridge? Bridge, bool CanReopen = false, Action? Reopen = null,
    bool Reopening = false, bool Connected = true);

/// <summary>
/// Reactor host for one session's <see cref="TerminalSurface"/>.
/// </summary>
/// <remarks>
/// The surface is embedded with <see cref="XamlHostElement"/> rather than a
/// <c>[GenerateReactorWrapper]</c> record: the control has no declarative props to
/// diff (it reads the emulator and the settings store itself), it must be created
/// exactly once per session, and painting happens on its own dispatcher callback.
/// The <see cref="XamlHostElement.TypeKey"/> carries the bridge identity so Reactor
/// can never reconcile one session's surface onto another session's bridge.
/// </remarks>
internal sealed class TerminalView : Component<TerminalViewProps>
{
    public override Element Render()
    {
        // Hooks must run unconditionally, before the capability guard below.
        AppServices services = UseContext(AppServices.ReactorContext)
            ?? throw new InvalidOperationException("The component must be inside the shell service context.");
        TerminalBridge? bridge = Props.Bridge;
        if (bridge is null)
        {
            return Placeholder(Str.T("terminal.unavailable", "This session does not expose a terminal."));
        }

        IAppSettingsStore settings = services.Settings;
        var logger = services.Logs.CreateLogger("TerminalSurface");

        // Rendering happens once per mount: the surface repaints itself from
        // TerminalBridge.Invalidated, so terminal output never re-renders Reactor.
        Element surface = new XamlHostElement(() => new TerminalSurface(bridge, settings, logger), UpdateSurface)
        {
            TypeKey = "TerminalSurface:" + bridge.GetHashCode(),
        };
        return Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
            bridge.IsClosed ? HStack(AppTheme.SpacingM,
                Body(Props.Connected ? Str.T("terminal.shellExited", "The shell exited. The connection is still online.")
                    : Str.T("terminal.connectionClosed", "The connection is closed. Reconnect to open a terminal.")),
                Props.CanReopen ? Button(Str.T("terminal.reopen", "Reopen shell"), Props.Reopen)
                    .AutomationName(Str.T("terminal.reopen", "Reopen shell"))
                    .IsEnabled(!Props.Reopening).AutomationId("ReopenShellButton") : null)
                .Padding(AppTheme.SpacingM).Grid(row: 0) : null,
            surface.Grid(row: 1));
    }

    private static void UpdateSurface(FrameworkElement element)
    {
        if (element is TerminalSurface surface)
        {
            surface.Refresh();
        }
    }

    private static Element Placeholder(string message) =>
        Border(
                VStack(
                        AppTheme.SpacingS,
                        Icon(FontIcon("\uE756", fontSize: 32)),
                        Body(message).TextWrapping(TextWrapping.Wrap))
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
            .Background(Theme.LayerFill)
            .Padding(AppTheme.SpacingXl);
}
