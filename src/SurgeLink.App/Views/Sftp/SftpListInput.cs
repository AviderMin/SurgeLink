using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SurgeLink.Core.Files;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Receives system double taps even when the item container handles them.</summary>
internal static class SftpListInput
{
    private sealed class Binding
    {
        public IReadOnlyList<RemoteFileEntry> Entries = Array.Empty<RemoteFileEntry>();
        public Action<RemoteFileEntry>? Open;
    }

    private static readonly ConditionalWeakTable<ListView, Binding> Bindings = new();

    private sealed class BlankAreaBinding
    {
        public Action? ClearSelection;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, BlankAreaBinding> BlankAreas = new();

    public static void AttachBlankArea(FrameworkElement area, Action clearSelection)
    {
        var binding = BlankAreas.GetValue(area, control =>
        {
            var state = new BlankAreaBinding();
            control.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (!args.GetCurrentPoint(control).Properties.IsLeftButtonPressed) { return; }
                var source = args.OriginalSource as DependencyObject;
                while (source is not null && source != control)
                {
                    // Rows and scroll controls retain their normal input behavior.
                    if (source is ListViewItem or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar
                        or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) { return; }
                    source = VisualTreeHelper.GetParent(source);
                }
                if (source == control) { state.ClearSelection?.Invoke(); }
            }), handledEventsToo: true);
            return state;
        });
        binding.ClearSelection = clearSelection;
    }


    public static void Attach(ListView list, IReadOnlyList<RemoteFileEntry> entries, Action<RemoteFileEntry> open)
    {
        var binding = Bindings.GetValue(list, control =>
        {
            var state = new Binding();
            control.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, args) =>
            {
                var source = args.OriginalSource as DependencyObject;
                while (source is not null && source is not ListViewItem && source != control)
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                if (source is ListViewItem container)
                {
                    var index = control.IndexFromContainer(container);
                    if (index >= 0 && index < state.Entries.Count)
                    {
                        args.Handled = true;
                        state.Open?.Invoke(state.Entries[index]);
                    }
                }
            }), handledEventsToo: true);
            return state;
        });
        binding.Entries = entries;
        binding.Open = open;
    }
}
