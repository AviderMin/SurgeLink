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
