using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>
/// Global keyboard shortcuts. The commands are hosted by a
/// <c>CommandHost</c> at the root of the shell, which registers the
/// accelerators for the whole window.
/// </summary>
internal static class ShellCommands
{
    public static Command[] Build(
        Action newSession,
        Action closeTab,
        Action nextTab,
        Action previousTab,
        Action openSettings) => new[]
    {
        new Command
        {
            Label = "New session",
            Description = "Open the new session dialog",
            Execute = newSession,
            Accelerator = Accelerator(VirtualKey.T, VirtualKeyModifiers.Control),
        },
        new Command
        {
            Label = "Close tab",
            Description = "Close the active session tab",
            Execute = closeTab,
            Accelerator = Accelerator(VirtualKey.W, VirtualKeyModifiers.Control),
        },
        new Command
        {
            Label = "Next tab",
            Description = "Switch to the next session tab",
            Execute = nextTab,
            Accelerator = Accelerator(VirtualKey.Tab, VirtualKeyModifiers.Control),
        },
        new Command
        {
            Label = "Previous tab",
            Description = "Switch to the previous session tab",
            Execute = previousTab,
            Accelerator = Accelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        },
        new Command
        {
            Label = "Settings",
            Description = "Open the settings page",
            Execute = openSettings,
            Accelerator = Accelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        },
    };
}
