using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
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
        string? closeText = null)
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

        return dialog.Set(DialogAutomation.Tag(automationPrefix));
    }
}
