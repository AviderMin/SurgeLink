using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Shell;

/// <summary>Keeps tab header alignment independent of the session content alignment.</summary>
internal static class SessionTabHeader
{
    private sealed class Header
    {
        public Grid Layout { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
        public Viewbox IconHost { get; } = new()
        {
            Width = 16,
            Height = 16,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        public TextBlock Title { get; } = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        public string? IconKey;
        public FontIcon AgentIcon { get; } = new()
        {
            Glyph = "\uE99A",
            FontSize = 14,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
        };

        public Header()
        {
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Title, 1);
            Grid.SetColumn(AgentIcon, 2);
            AutomationProperties.SetAccessibilityView(IconHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Layout.Children.Add(IconHost);
            Layout.Children.Add(Title);
            Layout.Children.Add(AgentIcon);
        }
    }

    private static readonly ConditionalWeakTable<TabViewItem, Header> Headers = new();

    public static void Apply(TabViewItem item, string title, string? operatingSystem, bool agentControlled = false)
    {
        var header = Headers.GetValue(item, _ => new Header());
        PrivacyDisplay.Apply(header.Title, title);
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
        var status = agentControlled ? Str.T("shell.agent.tabControlled", "Agent is controlling this session") : string.Empty;
        header.AgentIcon.Visibility = agentControlled ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(header.AgentIcon, status);
        AutomationProperties.SetAutomationId(header.AgentIcon, "SessionAgentIndicator");
        ToolTipService.SetToolTip(header.AgentIcon, status);
        if (!ReferenceEquals(item.Header, header.Layout)) { item.Header = header.Layout; }
        AutomationProperties.SetName(item, agentControlled
            ? Str.F("shell.agent.tabAccessible", "{0}, controlled by Agent", PrivacyDisplay.Mask(title))
            : PrivacyDisplay.Mask(title));
        AutomationProperties.SetHelpText(item, status);
        ToolTipService.SetToolTip(item, agentControlled ? title + "\n" + status : title);
    }
}
