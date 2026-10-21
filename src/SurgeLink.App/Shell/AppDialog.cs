using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Shared native dialog presentation for app preferences and information.</summary>
internal static class AppDialog
{
    public static Element Render(
        string title,
        Element content,
        string automationPrefix,
        Action<ContentDialogResult> onClosed,
        string primaryText,
        string? closeText = null,
        ElementTheme theme = ElementTheme.Default)
    {
        var dialog = ContentDialog(title,
            ScrollView(content).MaxHeight(420).HorizontalScrollMode(ScrollingScrollMode.Disabled),
            primaryButtonText: primaryText) with
        {
            IsOpen = true,
            CloseButtonText = closeText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result =>
            {
                // Native dialog teardown must finish before a callback removes its component.
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                if (dispatcher is null)
                {
                    onClosed(result);
                }
                else
                {
                    dispatcher.TryEnqueue(() => onClosed(result));
                }
            },
        };

        ContentDialog? nativeDialog = null;
        return dialog.Set(control =>
        {
            nativeDialog = control;
            control.RequestedTheme = theme;
            DialogAutomation.Tag(automationPrefix)(control);
        }).Opened(() =>
        {
            // Reactor owns the Opened subscription; tag again after its native
            // template is realized, including the first render of an About dialog.
            if (nativeDialog is { } control)
            {
                DialogAutomation.Tag(automationPrefix)(control);
                control.DispatcherQueue?.TryEnqueue(() => DialogAutomation.Tag(automationPrefix)(control));
            }
        });
    }
}
