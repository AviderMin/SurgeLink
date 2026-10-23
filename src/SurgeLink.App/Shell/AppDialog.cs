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
    // Match the session editor, including its margins in narrow windows.
    public const double ContentWidth = 640;
    public const double MaxWidth = 760;

    public static double FitContentWidth(double preferredWidth, double windowWidth) =>
        Math.Max(0, Math.Min(preferredWidth, windowWidth - 96));

    public static Element Render(
        string title,
        Element content,
        string automationPrefix,
        Action<ContentDialogResult> onClosed,
        string primaryText,
        string? closeText = null,
        ElementTheme theme = ElementTheme.Default,
        bool isOpen = true,
        double? contentWidth = null)
    {
        ScrollViewer? scroll = null;
        ContentDialog? nativeDialog = null;
        XamlRoot? sizingRoot = null;
        Windows.Foundation.TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? sizingHandler = null;
        void Resize()
        {
            if (nativeDialog?.XamlRoot is not { } root || scroll is null) { return; }
            // Leave space for the native title, padding, command row and window margins.
            nativeDialog.Resources["ContentDialogMaxHeight"] = Math.Max(200, root.Size.Height - 48);
            scroll.MaxHeight = Math.Max(80, Math.Min(480, root.Size.Height - 280));
            if (contentWidth is { } width) { scroll.Width = FitContentWidth(width, root.Size.Width); }
        }
        var body = ScrollViewer(Border(content).Padding(0, 0, 16, 24))
            .MaxHeight(320)
            .Set(viewer =>
            {
                scroll = viewer;
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                viewer.VerticalScrollMode = ScrollMode.Enabled;
                Resize();
            });
        var dialog = ContentDialog(title, body,
            primaryButtonText: primaryText) with
        {
            IsOpen = isOpen,
            CloseButtonText = closeText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result =>
            {
                if (sizingRoot is not null && sizingHandler is not null) { sizingRoot.Changed -= sizingHandler; }
                nativeDialog?.Resources.Remove("ContentDialogMaxHeight");
                if (contentWidth.HasValue) { nativeDialog?.Resources.Remove("ContentDialogMaxWidth"); }
                onClosed(result);
            },
        };

        return QueuedDialog.Wrap(dialog.Set(control =>
        {
            nativeDialog = control;
            if (contentWidth.HasValue)
            {
                control.Resources["ContentDialogMaxWidth"] = MaxWidth;
                if (scroll is not null) { scroll.Width = contentWidth.Value; }
            }
            control.RequestedTheme = theme;
            Resize();
            DialogAutomation.Tag(automationPrefix)(control);
        }).Opened(() =>
        {
            // Reactor owns the Opened subscription; tag again after its native
            // template is realized, including the first render of an About dialog.
            if (nativeDialog is { } control)
            {
                control.RequestedTheme = theme;
                Resize();
                if (control.XamlRoot is { } root)
                {
                    sizingRoot = root;
                    sizingHandler = (_, _) => Resize();
                    root.Changed += sizingHandler;
                }
                DialogAutomation.Tag(automationPrefix)(control);
                control.DispatcherQueue?.TryEnqueue(() =>
                {
                    control.RequestedTheme = theme;
                    Resize();
                    DialogAutomation.Tag(automationPrefix)(control);
                });
            }
        }));
    }

}
