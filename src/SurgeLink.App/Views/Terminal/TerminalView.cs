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
internal sealed record TerminalViewProps(TerminalBridge? Bridge);

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
        return new XamlHostElement(() => new TerminalSurface(bridge, settings, logger), UpdateSurface)
        {
            TypeKey = "TerminalSurface:" + bridge.GetHashCode(),
        };
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
