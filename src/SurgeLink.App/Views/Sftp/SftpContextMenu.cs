using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Replaces native callbacks when Reactor reconciles a control.</summary>
internal static class SftpContextMenu
{
    private sealed class Subscription
    {
        public TypedEventHandler<UIElement, ContextRequestedEventArgs>? Handler { get; set; }
    }
    private static readonly ConditionalWeakTable<FrameworkElement, Subscription> Subscriptions = new();

    public static void Attach(FrameworkElement element, Action<FrameworkElement, ContextRequestedEventArgs> callback)
    {
        var subscription = Subscriptions.GetValue(element, static control =>
        {
            var state = new Subscription();
            control.Unloaded += (_, _) =>
            {
                if (state.Handler is { } handler) { control.ContextRequested -= handler; }
                state.Handler = null;
            };
            return state;
        });
        if (subscription.Handler is { } previous) { element.ContextRequested -= previous; }
        subscription.Handler = (_, args) => callback(element, args);
        element.ContextRequested += subscription.Handler;
    }

    public static void Add(MenuFlyout menu, string label, string glyph, Action action, bool enabled)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
