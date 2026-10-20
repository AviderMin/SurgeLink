using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;

namespace SurgeLink.App.Styling;

/// <summary>
/// Maps the persisted <see cref="ThemeMode"/> onto a WinUI <see cref="ElementTheme"/>
/// and applies it to the root of the shell. WinUI cannot change
/// <c>Application.RequestedTheme</c> after startup, so theming is expressed as a
/// <c>RequestedTheme</c> on the root element — every Theme.* token below it
/// re-resolves, including High Contrast system brushes.
/// </summary>
internal static class ThemeController
{
    /// <summary>Converts the user preference into an element theme.</summary>
    public static ElementTheme ToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Applies the preference to a subtree (the shell root).</summary>
    public static Element Apply(Element root, ThemeMode mode) =>
        root.RequestedTheme(ToElementTheme(mode));

    /// <summary>Caption used in the settings page.</summary>
    public static string Describe(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => Str.T("shell.theme.alwaysLight", "Always light"),
        ThemeMode.Dark => Str.T("shell.theme.alwaysDark", "Always dark"),
        _ => Str.T("shell.theme.followSystem", "Follow Windows"),
    };
}
