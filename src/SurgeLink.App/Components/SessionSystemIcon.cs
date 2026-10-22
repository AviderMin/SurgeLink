using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Components;

/// <summary>Local vector silhouettes, rendered in the current theme's foreground.</summary>
internal static class SessionSystemIcon
{
    public static Element Render(string? operatingSystem)
    {
        var key = RemoteSystemIcon.Resolve(operatingSystem);
        return (SystemIconPaths.Data.TryGetValue(key, out var path)
                ? Icon(PathIcon(path))
                : Icon(FontIcon("\uE774", fontSize: 20)))
            .Width(24).Height(24)
            .AccessibilityView(AccessibilityView.Raw);
    }
}
