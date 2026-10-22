using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// Owns the single WinUI <see cref="UIElement.ContextRequested"/> subscription of
/// an element. Reactor re-runs <c>.Set()</c> on every update, so the callback is
/// replaced in place instead of being unsubscribed and re-added; an element that
/// WinUI unloads and loads again keeps working because the subscription is
/// restored on <c>Loaded</c>.
/// </summary>
internal static class SftpContextMenu
{
    private sealed class Subscription
    {
        public Action<FrameworkElement, ContextRequestedEventArgs>? Callback { get; set; }

        public TypedEventHandler<UIElement, ContextRequestedEventArgs>? Handler { get; set; }

        /// <summary>Subscribes once; later calls only keep the callback current.</summary>
        public void Subscribe(FrameworkElement element)
        {
            if (Handler is not null || Callback is null)
            {
                return;
            }

            Handler = (_, args) => Callback?.Invoke(element, args);
            element.ContextRequested += Handler;
        }

        /// <summary>Stops listening; a following <see cref="Subscribe"/> starts again.</summary>
        public void Unsubscribe(FrameworkElement element)
        {
            if (Handler is { } handler)
            {
                element.ContextRequested -= handler;
            }

            Handler = null;
        }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Subscription> Subscriptions = new();

    /// <summary>
    /// Makes <paramref name="callback"/> the element's context-menu handler. Safe
    /// to call on every render: the element keeps one handler whose callback is
    /// swapped, and the handler returns after an unload/load cycle of the element.
    /// </summary>
    public static void Attach(FrameworkElement element, Action<FrameworkElement, ContextRequestedEventArgs> callback)
    {
        var subscription = Subscriptions.GetValue(element, static control =>
        {
            var state = new Subscription();
            control.Loaded += (_, _) => state.Subscribe(control);
            control.Unloaded += (_, _) => state.Unsubscribe(control);
            return state;
        });

        subscription.Callback = callback;
        subscription.Subscribe(element);
    }

    public static void Add(MenuFlyout menu, string label, string glyph, Action action, bool enabled)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
