using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

internal sealed record AppDialogProps(
    string Title,
    Element Content,
    string AutomationPrefix,
    Action<ContentDialogResult> OnClosed,
    string PrimaryText,
    string? CloseText,
    ElementTheme Theme,
    bool IsOpen,
    double? ContentWidth,
    string? SecondaryText,
    Action<ContentDialog>? OnOpened);

/// <summary>Shared modal layout, scrolling and sizing for session, settings and about dialogs.</summary>
internal sealed class AppDialog : Component<AppDialogProps>
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
        double? contentWidth = null,
        string? secondaryText = null,
        Action<ContentDialog>? onOpened = null) =>
        Component<AppDialog, AppDialogProps>(new(title, content, automationPrefix, onClosed,
            primaryText, closeText, theme, isOpen, contentWidth, secondaryText, onOpened));

    public override Element Render()
    {
        var scroll = UseRef<ScrollViewer?>(null);
        var nativeDialog = UseRef<ContentDialog?>(null);
        var sizingRoot = UseRef<XamlRoot?>(null);
        var sizingHandler = UseRef<Windows.Foundation.TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>?>(null);
        var closed = UseRef(false);
        UseEffect(() => Cleanup, Array.Empty<object>());

        void Resize()
        {
            if (nativeDialog.Current?.XamlRoot is not { } root || scroll.Current is not { } viewer) { return; }
            // Leave space for the native title, padding, command row and window margins.
            nativeDialog.Current.Resources["ContentDialogMaxHeight"] = Math.Max(200, root.Size.Height - 48);
            viewer.MaxHeight = Math.Max(0, Math.Min(600, root.Size.Height - 240));
            if (Props.ContentWidth is { } width) { viewer.Width = FitContentWidth(width, root.Size.Width); }
        }

        void Cleanup()
        {
            if (sizingRoot.Current is { } root && sizingHandler.Current is { } handler) { root.Changed -= handler; }
            sizingRoot.Current = null;
            sizingHandler.Current = null;
            // Native dialogs are pooled; keep sizing overrides scoped to this modal.
            nativeDialog.Current?.Resources.Remove("ContentDialogMaxHeight");
            if (Props.ContentWidth.HasValue) { nativeDialog.Current?.Resources.Remove("ContentDialogMaxWidth"); }
        }

        var body = ScrollViewer(Border(Props.Content).Padding(0, 0, 16, 24))
            .Width(Props.ContentWidth ?? double.NaN)
            .MaxHeight(520)
            .Set(viewer =>
            {
                scroll.Current = viewer;
                viewer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.HorizontalScrollMode = ScrollMode.Disabled;
                viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                viewer.VerticalScrollMode = ScrollMode.Enabled;
                Resize();
            });
        var dialog = ContentDialog(Props.Title, body,
            primaryButtonText: Props.PrimaryText) with
        {
            IsOpen = Props.IsOpen && !closed.Current,
            SecondaryButtonText = Props.SecondaryText ?? string.Empty,
            CloseButtonText = Props.CloseText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result =>
            {
                if (closed.Current) return;
                closed.Current = true;
                Cleanup();
                Props.OnClosed(result);
            },
        };

        return QueuedDialog.Wrap(dialog.Set(control =>
        {
            nativeDialog.Current = control;
            if (Props.ContentWidth.HasValue)
            {
                control.Resources["ContentDialogMaxWidth"] = MaxWidth;
            }
            control.RequestedTheme = Props.Theme;
            Resize();
            DialogAutomation.Tag(Props.AutomationPrefix)(control);
            Props.OnOpened?.Invoke(control);
            control.Resources["AppDialogAttachMarker"] = true;
        }).Opened(() =>
        {
            // Reactor owns the Opened subscription; tag again after its native
            // template is realized, including the first render of an About dialog.
            if (nativeDialog.Current is { } control)
            {
                control.RequestedTheme = Props.Theme;
                Resize();
                if (control.XamlRoot is { } root)
                {
                    sizingRoot.Current = root;
                    sizingHandler.Current = (_, _) => Resize();
                    root.Changed += sizingHandler.Current;
                }
                DialogAutomation.Tag(Props.AutomationPrefix)(control);
                Props.OnOpened?.Invoke(control);
                control.DispatcherQueue?.TryEnqueue(() =>
                {
                    if (closed.Current) return;
                    control.RequestedTheme = Props.Theme;
                    Resize();
                    DialogAutomation.Tag(Props.AutomationPrefix)(control);
                });
            }
        }));
    }

}
