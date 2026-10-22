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
    Action ToggleSidebar,
    Action NewSession,
    Action OpenSettings,
    Action ShowAbout);

/// <summary>
/// The Windows title bar: app name, a live subtitle (active session + host) and
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
                    ? ProgressRing().Width(16).Height(16)
                        .AutomationName(Str.T("shell.titleBar.busy", "Working"))
                    : Empty(),
                IconButton("\uE700", Str.T("shell.titleBar.toggleSidebar", "Toggle sidebar"), "B", props.ToggleSidebar),
                IconButton("\uE710", Str.T("shell.titleBar.newSession", "New session"), "N", props.NewSession),
                IconButton("\uE713", Str.T("shell.titleBar.settings", "Settings"), "S", props.OpenSettings),
                IconButton("\uE946", Str.T("shell.titleBar.about", "About SurgeLink"), "A", props.ShowAbout))
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, AppTheme.SpacingS, 0);

        return TitleBar("SurgeLink")
            .Set(titleBar => titleBar.IconSource = new Microsoft.UI.Xaml.Controls.BitmapIconSource
            {
                UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png")),
                ShowAsMonochrome = false,
            })
            .Subtitle(props.Subtitle)
            .RightHeader(right);
    }

    private static Element IconButton(string glyph, string label, string accessKey, Action onClick) =>
        Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .ToolTip(Str.F("shell.titleBar.shortcut", "{0} (Alt+{1})", label, accessKey))
            .AutomationName(label)
            .AccessKey(accessKey)
            .IsDragRegion(false);
}
