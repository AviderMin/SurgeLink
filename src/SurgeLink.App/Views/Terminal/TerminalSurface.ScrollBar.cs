using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Windows Terminal's native scrollbar template, ported to WinUI 3.</summary>
internal sealed partial class TerminalSurface
{
    private static ResourceDictionary? _scrollBarResources;

    private static Style GetScrollBarStyle()
    {
        if (_scrollBarResources is null)
        {
            using var stream = typeof(TerminalSurface).Assembly.GetManifestResourceStream(
                "SurgeLink.WindowsTerminalScrollBar.xaml")
                ?? throw new InvalidOperationException("The terminal scrollbar template is missing.");
            using var reader = new StreamReader(stream);
            _scrollBarResources = (ResourceDictionary)XamlReader.Load(reader.ReadToEnd());
        }

        return (Style)_scrollBarResources["WindowsTerminalScrollBarStyle"];
    }
}
