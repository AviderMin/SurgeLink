using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using SurgeLink.App.Components;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Shell;

/// <summary>Keeps tab header alignment independent of the session content alignment.</summary>
internal static class SessionTabHeader
{
    private sealed class Header
    {
        public Grid Layout { get; } = new() { VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 8 };
        public Viewbox IconHost { get; } = new()
        {
            Width = 16,
            Height = 16,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        public TextBlock Title { get; } = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        public string? IconKey;

        public Header()
        {
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(Title, 1);
            AutomationProperties.SetAccessibilityView(IconHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Layout.Children.Add(IconHost);
            Layout.Children.Add(Title);
        }
    }

    private static readonly ConditionalWeakTable<TabViewItem, Header> Headers = new();

    public static void Apply(TabViewItem item, string title, string? operatingSystem)
    {
        var header = Headers.GetValue(item, _ => new Header());
        header.Title.Text = title;
        var key = RemoteSystemIcon.Resolve(operatingSystem);
        if (header.IconKey != key)
        {
            header.IconHost.Child = SystemIconPaths.Data.TryGetValue(key, out var path)
                // Render the complete geometry at its design size, then scale it.
                // PathIcon keeps its intrinsic geometry size and clips in a small host.
                ? (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                    "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Width=\"24\" Height=\"24\" Stretch=\"Uniform\" " +
                    "Fill=\"{ThemeResource TextFillColorPrimaryBrush}\" Data=\"" + path + "\" />")
                : new FontIcon { Glyph = "\uE774", FontSize = 16 };
            header.IconKey = key;
        }
        if (!ReferenceEquals(item.Header, header.Layout)) { item.Header = header.Layout; }
        AutomationProperties.SetName(item, title);
        ToolTipService.SetToolTip(item, title);
    }
}
