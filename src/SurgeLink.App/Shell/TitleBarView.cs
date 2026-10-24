using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for <see cref="TitleBarView"/>.</summary>
internal sealed record TitleBarProps(
    string Subtitle,
    bool IsBusy,
    Action CancelConnection,
    Action ToggleSidebar,
    Action NewSession,
    Action OpenSettings,
    Action ShowAbout,
    Action ToggleAgent);

/// <summary>
/// The Windows title bar: app name, connected server count and
/// the global actions. Kept declarative so the caption buttons and drag regions
/// stay native.
/// </summary>
internal static class TitleBarView
{
    public static Element Render(TitleBarProps props)
    {
        var right = HStack(
                AppTheme.SpacingS,
                props.IsBusy
                    ? Button(Str.T("common.cancel", "Cancel"), props.CancelConnection)
                        .AutomationName(Str.T("common.cancel", "Cancel"))
                        .IsDragRegion(false)
                    : Empty(),
                props.IsBusy
                    ? ProgressRing().Width(16).Height(16)
                        .AutomationName(Str.T("shell.titleBar.busy", "Working"))
                    : Empty(),
                IconButton("\uE99A", Str.T("shell.agent.title", "Agent"), "G", props.ToggleAgent)
                    .AutomationId("AgentButton"),
                IconButton("\uE700", Str.T("shell.titleBar.toggleSidebar", "Toggle sidebar"), "B", props.ToggleSidebar),
                IconButton("\uE710", Str.T("shell.titleBar.newSession", "New session"), "N", props.NewSession),
                IconButton("\uE713", Str.T("shell.titleBar.settings", "Settings"), "S", props.OpenSettings),
                IconButton("\uE946", Str.T("shell.titleBar.about", "About SurgeLink"), "A", props.ShowAbout))
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, AppTheme.SpacingS, 0);

        return TitleBar("SurgeLink")
            // Let Reactor own icon updates; a raw setter would replace the image
            // on every shell refresh and compete with its default window icon.
            .Icon(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png")).AbsoluteUri)
            .Subtitle(props.Subtitle)
            .ToolTip(props.Subtitle)
            .RightHeader(right);
    }

    private static Element IconButton(string glyph, string label, string accessKey, Action onClick) =>
        Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .Width(32).Height(32).MinWidth(32).MinHeight(32)
            .Padding(0)
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
            })
            .ToolTip(Str.F("shell.titleBar.shortcut", "{0} (Alt+{1})", label, accessKey))
            .AutomationName(label)
            .AccessKey(accessKey)
            .IsDragRegion(false);
}
